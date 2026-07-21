using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;
using SealedFga.AuthModel;
using SealedFga.Exceptions;
using SealedFga.Util;
using Tuple = OpenFga.Sdk.Model.Tuple;

namespace SealedFga.Fga;

/// <summary>
///     Wrapper class for communicating with the OpenFGA service using strongly typed IDs.
///     Handles reads directly; queues writes/deletes for reliable processing.
/// </summary>
/// <param name="openFgaClient">The configured OpenFGA client.</param>
/// <param name="options">The SealedFGA options.</param>
/// <param name="modelCache">
///     The shared authorization-model cache (DI registers a singleton). <c>null</c> — e.g. direct
///     construction in tests — falls back to a private per-instance cache, which for a scoped
///     service means effectively no cross-request caching.
/// </param>
public class SealedFgaService(
    OpenFgaClient openFgaClient,
    IOptions<SealedFgaOptions> options,
    SealedFgaAuthModelCache? modelCache = null
) : ISealedFgaService {
    private readonly SealedFgaAuthModelCache _modelCache = modelCache ?? new SealedFgaAuthModelCache();

    private int MaxTuplesPerWrite => Math.Max(1, options.Value.MaxTuplesPerWrite);

    /// <summary>
    ///     Resolves the effective read consistency for a <b>list-shaped</b> operation: the per-call
    ///     value wins, falling back to <see cref="SealedFgaOptions.DefaultListConsistency" />. Single
    ///     <c>Check</c>/<c>BatchCheck</c> calls deliberately do not use this.
    /// </summary>
    private ConsistencyPreference? ResolveListConsistency(SealedFgaQueryOptions? queryOptions)
        => queryOptions?.Consistency ?? options.Value.DefaultListConsistency;


    #region Strongly-Typed ID Methods

    /// <inheritdoc />
    public Task DeleteObjectFromOpenFgaIncludingAllRelations<TObjId>(TObjId objId)
        where TObjId : ISealedFgaTypeId<TObjId>
        => DeleteAllRelationsForRawObjectAsync(
            objId.AsOpenFgaIdTupleString(),
            IdUtil.GetNameByIdType(typeof(TObjId))
        );

    /// <inheritdoc />
    public async Task DeleteAllRelationsForRawObjectAsync(
        string rawObjectId,
        string typeName,
        CancellationToken cancellationToken = new()
    ) {
        // Stored tuples where the object appears as the object ...
        var relationTuples = await ListAllRelationsToObjectAsync(rawObjectId, cancellationToken);
        // ... and stored tuples where it appears as the user/subject.
        relationTuples.AddRange(
            await ListAllRelationsFromUserAsync(rawObjectId, typeName, cancellationToken)
        );

        // Nuke all relations from and to the object; effectively deleting it from OpenFGA.
        // DistinctTuples matters: ignore semantics only cover store-level conflicts, the server
        // still rejects duplicate tuples within a single request.
        await DeleteTuplesAsync(DistinctTuples(relationTuples), cancellationToken);
    }


    /// <inheritdoc />
    public async Task EnsureCheckAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId> {
        if (!await CheckAsync(user, relation, objectId, queryOptions, cancellationToken)) {
            throw new FgaForbiddenException(user, relation, objectId);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CheckAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId>
        => await CheckAsync(new TupleKey {
                User = user.AsOpenFgaIdTupleString(),
                Relation = relation.AsOpenFgaString(),
                Object = objectId.AsOpenFgaIdTupleString(),
            },
            queryOptions,
            cancellationToken
        );

    /// <inheritdoc />
    public async Task<IEnumerable<TObjId>> ListObjectsAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId> {
        var objectStrings = await ListObjectsAsync(
            user.AsOpenFgaIdTupleString(),
            relation.AsOpenFgaString(),
            IdUtil.GetNameByIdType(typeof(TObjId)),
            queryOptions,
            cancellationToken
        );

        // OpenFGA returns full "type:id" object strings; strip the type prefix before parsing the raw ID.
        return objectStrings.Select(obj => {
                var separatorIndex = obj.IndexOf(':');
                var rawId = separatorIndex >= 0 ? obj.Substring(separatorIndex + 1) : obj;
                return IdUtil.ParseId<TObjId>(rawId);
            }
        );
    }

    /// <inheritdoc />
    public async Task<SealedFgaListUsersResult<TUserId>> ListUsersAsync<TObjId, TUserId>(
        TObjId objectId,
        ISealedFgaRelation<TObjId> relation,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId>
        where TUserId : ISealedFgaTypeId<TUserId> {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(objectId);

        var userTypeName = IdUtil.GetNameByIdType(typeof(TUserId));
        var users = await ListUsersAsync(
            new FgaObject {
                Type = IdUtil.GetNameByIdType(typeof(TObjId)),
                Id = objectId.ToString(),
            },
            relation.AsOpenFgaString(),
            [new UserTypeFilter { Type = userTypeName }],
            queryOptions,
            cancellationToken
        );

        return MapListUsersResult<TUserId>(users);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ISealedFgaRelation<TObjId>>> ListRelationsAsync<TObjId>(
        ISealedFgaUser user,
        IEnumerable<ISealedFgaRelation<TObjId>> relations,
        TObjId objectId,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId> {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(objectId);

        var relationList = relations.ToList();
        if (relationList.Count == 0) {
            return [];
        }

        var rawUser = user.AsOpenFgaIdTupleString();
        var rawObject = objectId.AsOpenFgaIdTupleString();
        var tupleKeys = relationList.Select(relation => new TupleKey {
                User = rawUser,
                Relation = relation.AsOpenFgaString(),
                Object = rawObject,
            }
        ).ToList();

        // Shared contextual tuples for every relation; list ops honor DefaultListConsistency.
        var contextualTuples = queryOptions?.ToContextualTupleKeys();
        var results = (await BatchCheckCoreAsync(
            tupleKeys,
            _ => contextualTuples,
            ResolveListConsistency(queryOptions),
            cancellationToken
        )).ToList();

        return relationList.Where((_, index) => results[index]).ToList();
    }

    /// <inheritdoc />
    public async Task<Dictionary<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object), bool>>
        BatchCheckAsync<TObjId>(
            IEnumerable<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object)> checks,
            SealedFgaQueryOptions? queryOptions = null,
            CancellationToken cancellationToken = new()
        )
        where TObjId : ISealedFgaTypeId<TObjId> {
        var checksAsList = checks.ToList();
        var contextualTuples = queryOptions?.ToContextualTupleKeys();
        var results = (await BatchCheckCoreAsync(
            ToBatchTupleKeys(checksAsList),
            _ => contextualTuples,
            queryOptions?.Consistency,
            cancellationToken
        )).ToList();

        return MapResultsByCheck(checksAsList, results);
    }

    /// <inheritdoc />
    public async Task<Dictionary<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object), bool>>
        BatchCheckAsync<TObjId>(
            IEnumerable<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object)> checks,
            Func<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object),
                IReadOnlyCollection<SealedFgaContextualTuple>?> contextualTuplesFactory,
            ConsistencyPreference? consistency = null,
            CancellationToken cancellationToken = new()
        )
        where TObjId : ISealedFgaTypeId<TObjId> {
        ArgumentNullException.ThrowIfNull(contextualTuplesFactory);

        var checksAsList = checks.ToList();
        var results = (await BatchCheckCoreAsync(
            ToBatchTupleKeys(checksAsList),
            index => SealedFgaContextualTuple.ToContextualTupleKeys(contextualTuplesFactory(checksAsList[index])),
            consistency,
            cancellationToken
        )).ToList();

        return MapResultsByCheck(checksAsList, results);
    }

    /// <summary>Projects typed checks to raw batch tuple keys, preserving order.</summary>
    private static List<TupleKey> ToBatchTupleKeys<TObjId>(
        List<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object)> checks
    )
        where TObjId : ISealedFgaTypeId<TObjId>
        => checks.Select(check => new TupleKey {
                User = check.User.AsOpenFgaIdTupleString(),
                Relation = check.Relation.AsOpenFgaString(),
                Object = check.Object.AsOpenFgaIdTupleString(),
            }
        ).ToList();

    /// <summary>
    ///     Maps positional batch results back to their checks. Uses the indexer (not IndexOf) so
    ///     duplicate checks collapse to a single entry instead of throwing or mis-mapping.
    /// </summary>
    private static Dictionary<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object), bool>
        MapResultsByCheck<TObjId>(
            List<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object)> checks,
            List<bool> results
        )
        where TObjId : ISealedFgaTypeId<TObjId> {
        var resultsByCheck =
            new Dictionary<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object), bool>();
        for (var i = 0; i < checks.Count; i++) {
            var check = checks[i];
            resultsByCheck[(check.User, check.Relation, check.Object)] = results[i];
        }

        return resultsByCheck;
    }

    /// <summary>Maps an OpenFGA <c>ListUsers</c> response to the typed, wildcard-aware result.</summary>
    internal static SealedFgaListUsersResult<TUserId> MapListUsersResult<TUserId>(IEnumerable<User> users)
        where TUserId : ISealedFgaTypeId<TUserId> {
        var concrete = new List<TUserId>();
        var usersets = new List<SealedFgaListUsersUserset<TUserId>>();
        var hasWildcard = false;

        foreach (var user in users) {
            if (user.Object is not null) {
                concrete.Add(IdUtil.ParseId<TUserId>(user.Object.Id));
            } else if (user.Userset is not null) {
                usersets.Add(new SealedFgaListUsersUserset<TUserId>(
                    IdUtil.ParseId<TUserId>(user.Userset.Id),
                    user.Userset.Relation
                ));
            } else if (user.Wildcard is not null) {
                hasWildcard = true;
            }
        }

        return new SealedFgaListUsersResult<TUserId>(concrete, usersets, hasWildcard);
    }

    #endregion

    #region Raw String Methods

    /// <summary>
    ///     Reads tuples from OpenFGA directly using raw strings (not queued).
    /// </summary>
    /// <param name="rawUser">Optional user filter</param>
    /// <param name="rawRelation">Optional relation filter</param>
    /// <param name="rawObject">Optional object filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of tuples matching the criteria</returns>
    internal async Task<IEnumerable<Tuple>> ReadAsync(
        string? rawUser = null,
        string? rawRelation = null,
        string? rawObject = null,
        CancellationToken cancellationToken = new()
    ) {
        var request = new ClientReadRequest {
            User = rawUser,
            Relation = rawRelation,
            Object = rawObject,
        };

        // Follow the continuation token so callers see every stored tuple, not just the first page.
        return await ReadAllPagesAsync(request, cancellationToken);
    }

    /// <summary>
    ///     Checks authorization using raw strings.
    /// </summary>
    /// <param name="tuple">The tuple to check</param>
    /// <param name="queryOptions">Optional per-call options (contextual tuples, consistency)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the relation exists, false otherwise</returns>
    internal async Task<bool> CheckAsync(
        TupleKey tuple,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    ) {
        var response = await openFgaClient.Check(new ClientCheckRequest {
                User = tuple.User,
                Relation = tuple.Relation,
                Object = tuple.Object,
                ContextualTuples = queryOptions?.ToClientTupleKeys(),
            },
            queryOptions is null ? null : new ClientCheckOptions { Consistency = queryOptions.Consistency },
            cancellationToken
        );

        return response.Allowed ?? false;
    }

    /// <summary>
    ///     Lists objects that a user has a specific relation to using raw strings.
    /// </summary>
    /// <param name="rawUser">The user string</param>
    /// <param name="rawRelation">The relation string</param>
    /// <param name="objectTypeName">The type of objects to list</param>
    /// <param name="queryOptions">Optional per-call options (contextual tuples, consistency)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of object strings</returns>
    internal async Task<IEnumerable<string>> ListObjectsAsync(
        string rawUser,
        string rawRelation,
        string objectTypeName,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    ) {
        var consistency = ResolveListConsistency(queryOptions);
        var response = await openFgaClient.ListObjects(new ClientListObjectsRequest {
                User = rawUser,
                Relation = rawRelation,
                Type = objectTypeName,
                ContextualTuples = queryOptions?.ToClientTupleKeys(),
            },
            consistency is null ? null : new ClientListObjectsOptions { Consistency = consistency },
            cancellationToken
        );

        return response.Objects;
    }

    /// <summary>
    ///     Lists the subjects related to an object using raw request parts. Single-shot (no
    ///     continuation token in the SDK response); honors <see cref="SealedFgaOptions.DefaultListConsistency" />.
    /// </summary>
    /// <param name="fgaObject">The object whose subjects are listed.</param>
    /// <param name="rawRelation">The relation string.</param>
    /// <param name="userFilters">The user-type filters constraining which subject types are returned.</param>
    /// <param name="queryOptions">Optional per-call options (contextual tuples, consistency).</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The raw OpenFGA users from the response (empty when none).</returns>
    internal async Task<List<User>> ListUsersAsync(
        FgaObject fgaObject,
        string rawRelation,
        List<UserTypeFilter> userFilters,
        SealedFgaQueryOptions? queryOptions = null,
        CancellationToken cancellationToken = new()
    ) {
        var consistency = ResolveListConsistency(queryOptions);
        var response = await openFgaClient.ListUsers(new ClientListUsersRequest {
                Object = fgaObject,
                Relation = rawRelation,
                UserFilters = userFilters,
                ContextualTuples = queryOptions?.ToClientTupleKeys(),
            },
            consistency is null ? null : new ClientListUsersOptions { Consistency = consistency },
            cancellationToken
        );

        return response.Users ?? [];
    }

    /// <summary>
    ///     Retrieves all relations associated with a specific object.
    /// </summary>
    /// <param name="rawObject">The ID of the object for which relations are to be listed.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation if needed.</param>
    /// <returns>
    ///     An asynchronous task containing a collection of tuples representing relations tied to the specified object.
    /// </returns>
    internal async Task<List<TupleKey>> ListAllRelationsToObjectAsync(
        string rawObject,
        CancellationToken cancellationToken = new()
    ) {
        var readRequest = new ClientReadRequest {
            Object = rawObject,
        };

        var tuples = await ReadAllPagesAsync(readRequest, cancellationToken);
        return tuples.Select(t => t.Key).ToList();
    }

    /// <summary>
    ///     Retrieves all relation tuples where the specified user is related to other objects within the FGA system.
    /// </summary>
    /// <param name="rawUser">The ID of the user for whom relations are to be retrieved</param>
    /// <param name="userTypeName">The type name of the user</param>
    /// <param name="cancellationToken">Cancellation token to observe while waiting for the task to complete</param>
    /// <returns>A task that represents the asynchronous operation, containing a list of relation tuples</returns>
    internal async Task<List<TupleKey>> ListAllRelationsFromUserAsync(
        string rawUser,
        string userTypeName,
        CancellationToken cancellationToken = new()
    ) {
        // Determine which object types can reference our type as a directly related user. The
        // model is served from the shared cache (indefinite when the client pins a model ID,
        // TTL-bounded otherwise) instead of a fresh server read per call.
        var authModel = await _modelCache.GetModelAsync(
            openFgaClient.AuthorizationModelId,
            options.Value.AuthorizationModelCacheTtl,
            async () => {
                var response = await openFgaClient.ReadAuthorizationModel(cancellationToken: cancellationToken);
                return response.AuthorizationModel!;
            }
        );
        var typeDefinitions = authModel.TypeDefinitions;

        var relationTuples = new List<TupleKey>();
        foreach (var typeDef in typeDefinitions) {
            var referencesOurType = typeDef.Metadata?.Relations?.Values
                                           .Any(rel => rel.DirectlyRelatedUserTypes?
                                                          .Any(rt => rt.Type == userTypeName) ?? false)
                                    ?? false;
            if (!referencesOurType) {
                continue;
            }

            // Read STORED tuples where our object is the user on any object of this type. Using Read
            // (not ListObjects) ensures we only get physically stored tuples, never computed ones that
            // could not actually be deleted.
            var tuples = await ReadAllPagesAsync(
                new ClientReadRequest {
                    User = rawUser,
                    Object = $"{typeDef.Type}:",
                },
                cancellationToken
            );
            relationTuples.AddRange(tuples.Select(t => t.Key));
        }

        return relationTuples;
    }

    /// <summary>
    ///     The core batch-check path (raw strings). Contextual tuples are resolved <b>per item</b> via
    ///     <paramref name="contextualTuplesByIndex" /> — this is what makes both a single shared tuple
    ///     set and per-object tuple sets expressible over the same request.
    /// </summary>
    /// <param name="checks">Check requests, in the order results are mapped back to.</param>
    /// <param name="contextualTuplesByIndex">
    ///     Returns the contextual tuples for the check at the given index (or <c>null</c> for none).
    /// </param>
    /// <param name="consistency">Optional read consistency applied to the whole batch.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Results in the same order as input checks</returns>
    internal async Task<IEnumerable<bool>> BatchCheckCoreAsync(
        IReadOnlyList<TupleKey> checks,
        Func<int, ContextualTupleKeys?> contextualTuplesByIndex,
        ConsistencyPreference? consistency,
        CancellationToken cancellationToken
    ) {
        if (checks.Count == 0) {
            return [];
        }

        // Native server-side batch check. Each item carries its ordinal index as a correlation ID
        // (matching OpenFGA's ^[\w\d-]{1,36}$ constraint) so results — which may come back in any
        // order — map deterministically back to the input order. The SDK auto-chunks to the server's
        // batch limit and bounds parallelism internally, so no manual fan-out is needed.
        var request = new ClientBatchCheckRequest {
            Checks = checks.Select((check, index) => new ClientBatchCheckItem {
                    User = check.User,
                    Relation = check.Relation,
                    Object = check.Object,
                    ContextualTuples = contextualTuplesByIndex(index),
                    CorrelationId = index.ToString(CultureInfo.InvariantCulture),
                }
            ).ToList(),
        };

        var response = await openFgaClient.BatchCheck(
            request,
            consistency is null ? null : new ClientBatchCheckOptions { Consistency = consistency },
            cancellationToken
        );

        return MapBatchCheckResults(response.Result, checks.Count);
    }

    /// <summary>
    ///     Maps a batch-check response back to the input order via the ordinal correlation IDs.
    ///     The response must be complete and well-formed: a per-item error, a missing, duplicate,
    ///     or unparseable correlation ID must NOT be silently read as "not allowed" — that would
    ///     let a transient failure masquerade as "tuple does not exist" and corrupt callers that
    ///     act on the results. Any such response throws <see cref="FgaBatchCheckException" /> so
    ///     the operation can be retried.
    /// </summary>
    internal static bool[] MapBatchCheckResults(
        IEnumerable<ClientBatchCheckSingleResponse> responseItems,
        int requestCount
    ) {
        var results = new bool[requestCount];
        var covered = new bool[requestCount];
        foreach (var single in responseItems) {
            if (single.Error != null) {
                throw new FgaBatchCheckException(single.Request, single.Error);
            }

            if (!int.TryParse(
                    single.CorrelationId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var index
                )
                || index < 0
                || index >= requestCount) {
                throw new FgaBatchCheckException(
                    $"Batch check response contains an unknown correlation ID '{single.CorrelationId}' "
                    + $"(expected 0..{requestCount - 1})."
                );
            }

            if (covered[index]) {
                throw new FgaBatchCheckException(
                    $"Batch check response contains a duplicate correlation ID '{single.CorrelationId}'."
                );
            }

            covered[index] = true;
            results[index] = single.Allowed;
        }

        var missing = new List<string>();
        for (var i = 0; i < requestCount; i++) {
            if (!covered[i]) {
                missing.Add(i.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (missing.Count > 0) {
            throw new FgaBatchCheckException(
                $"Batch check response is incomplete: no result for correlation ID(s) {string.Join(", ", missing)}."
            );
        }

        return results;
    }

    #endregion

    #region Write/Delete Methods

    /// <inheritdoc />
    public async Task WriteTuplesAsync(
        List<TupleKey> tuples,
        CancellationToken ct = new()
    ) {
        var failures = await WriteAndDeleteTuplesWithOutcomesAsync(tuples, [], ct);
        if (failures.Count > 0) {
            throw new FgaWriteException(failures);
        }
    }

    /// <inheritdoc />
    public async Task DeleteTuplesAsync(List<TupleKey> tuples, CancellationToken ct = new()) {
        var failures = await WriteAndDeleteTuplesWithOutcomesAsync([], tuples, ct);
        if (failures.Count > 0) {
            throw new FgaWriteException(failures);
        }
    }

    /// <summary>
    ///     Applies writes and deletes as a <b>non-transactional</b> OpenFGA write: the SDK splits
    ///     the operations into chunks of <see cref="MaxTuplesPerWrite" /> (OpenFGA rejects a single
    ///     request above its per-write limit, default 100) and applies them as independent
    ///     requests, so a large operation is not atomic across chunks — safe here because ignore
    ///     semantics make the operations idempotent and re-runnable by the outbox drainer.
    ///     In this mode the SDK reports failures per tuple instead of throwing; the failed items
    ///     are returned so callers can attribute failures to individual tuples — the public
    ///     write/delete methods convert a non-empty failure list into an
    ///     <see cref="FgaWriteException" />, while the outbox drainer maps failures back to the
    ///     originating rows for per-row retry bookkeeping.
    /// </summary>
    /// <returns>The failed response items; empty when every tuple was applied.</returns>
    internal async Task<List<ClientWriteSingleResponse>> WriteAndDeleteTuplesWithOutcomesAsync(
        List<TupleKey> writeTuples,
        List<TupleKey> deleteTuples,
        CancellationToken ct
    ) {
        if (writeTuples.Count == 0 && deleteTuples.Count == 0) {
            return [];
        }

        var response = await openFgaClient.Write(
            new ClientWriteRequest {
                Writes = writeTuples.Select(tuple => new ClientTupleKey {
                        User = tuple.User,
                        Relation = tuple.Relation,
                        Object = tuple.Object,
                    }
                ).ToList(),
                Deletes = deleteTuples.Select(tuple => new ClientTupleKeyWithoutCondition {
                        User = tuple.User,
                        Relation = tuple.Relation,
                        Object = tuple.Object,
                    }
                ).ToList(),
            },
            new ClientWriteOptions {
                Transaction = new TransactionOptions {
                    Disable = true,
                    MaxPerChunk = MaxTuplesPerWrite,
                },
                Conflict = new ConflictOptions {
                    OnDuplicateWrites = OnDuplicateWrites.Ignore,
                    OnMissingDeletes = OnMissingDeletes.Ignore,
                },
            },
            ct
        );

        return response.Writes
                       .Concat(response.Deletes)
                       .Where(r => r.Status != ClientWriteStatus.SUCCESS)
                       .ToList();
    }

    #endregion Write/Delete Methods

    #region Helpers

    /// <summary>
    ///     Reads <b>all</b> pages of tuples matching a read request, following OpenFGA's
    ///     continuation token until the store is exhausted. OpenFGA's <c>Read</c> is paginated, so
    ///     a single call only returns the first page; callers that must see every stored tuple
    ///     (entity deletion) would otherwise silently truncate large relation sets.
    /// </summary>
    private async Task<List<Tuple>> ReadAllPagesAsync(
        ClientReadRequest request,
        CancellationToken cancellationToken
    ) {
        var allTuples = new List<Tuple>();
        string? continuationToken = null;
        do {
            var response = await openFgaClient.Read(
                request,
                new ClientReadOptions { ContinuationToken = continuationToken },
                cancellationToken
            );
            allTuples.AddRange(response.Tuples);
            continuationToken = response.ContinuationToken;
        } while (!string.IsNullOrEmpty(continuationToken));

        return allTuples;
    }

    /// <summary>
    ///     De-duplicates tuples by their (User, Relation, Object) identity.
    /// </summary>
    internal static List<TupleKey> DistinctTuples(IEnumerable<TupleKey> tuples)
        => tuples
          .GroupBy(t => (t.User, t.Relation, t.Object))
          .Select(g => g.First())
          .ToList();

    #endregion Helpers
}
