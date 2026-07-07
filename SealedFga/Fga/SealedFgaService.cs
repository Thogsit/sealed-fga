using System;
using System.Collections.Generic;
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
public class SealedFgaService(
    OpenFgaClient openFgaClient
) {

    #region Strongly-Typed ID Methods

    /// <summary>
    ///     Modifies all tuples containing a reference to the old ID and modifies them to reference the new ID.
    /// </summary>
    /// <param name="oldId">The current identifier to be updated.</param>
    /// <param name="newId">The new identifier to replace the old one.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <typeparam name="TId">The type of the identifier, which implements <see cref="ISealedFgaTypeId{TId}" />.</typeparam>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task ModifyIdAsync<TId>(
        TId oldId,
        TId newId,
        CancellationToken cancellationToken = new()
    ) where TId : ISealedFgaTypeId<TId>
        => await ModifyIdAsync(
            oldId.AsOpenFgaIdTupleString(),
            newId.AsOpenFgaIdTupleString(),
            IdUtil.GetNameByIdType(typeof(TId)),
            cancellationToken
        );

    /// <summary>
    ///     Deletes all relations that contain the given ID as a User or Object.
    /// </summary>
    /// <param name="objId">The ID to fully delete all related relations of.</param>
    /// <typeparam name="TObjId">The type of the ID.</typeparam>
    public Task DeleteObjectFromOpenFgaIncludingAllRelations<TObjId>(TObjId objId)
        where TObjId : ISealedFgaTypeId<TObjId>
        => DeleteAllRelationsForRawObjectAsync(
            objId.AsOpenFgaIdTupleString(),
            IdUtil.GetNameByIdType(typeof(TObjId))
        );

    /// <summary>
    ///     Deletes every <b>stored</b> relationship tuple in which the given raw object appears, either
    ///     as the object or as the user/subject. Used by the outbox drainer to purge a deleted entity.
    /// </summary>
    /// <param name="rawObjectId">The object's OpenFGA tuple string (<c>type:id</c>).</param>
    /// <param name="typeName">The object's OpenFGA type name.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
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
        await SafeDeleteTupleAsync(DistinctTuples(relationTuples), cancellationToken);
    }


    /// <summary>
    ///     Ensures authorization using strongly typed IDs, throwing an exception if the check fails.
    /// </summary>
    /// <param name="user">The user ID (strongly typed)</param>
    /// <param name="relation">The relation string</param>
    /// <param name="objectId">The object ID (strongly typed)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <exception cref="FgaForbiddenException">Thrown when the authorization check fails</exception>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task EnsureCheckAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId> {
        if (!await CheckAsync(user, relation, objectId, cancellationToken)) {
            throw new FgaForbiddenException(user, relation, objectId);
        }
    }

    /// <summary>
    ///     Checks authorization using strongly typed IDs.
    /// </summary>
    /// <param name="user">The user ID (strongly typed)</param>
    /// <param name="relation">The relation string</param>
    /// <param name="objectId">The object ID (strongly typed)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <returns>True if the relation exists, false otherwise</returns>
    public async Task<bool> CheckAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId>
        => await CheckAsync(new TupleKey {
                User = user.AsOpenFgaIdTupleString(),
                Relation = relation.AsOpenFgaString(),
                Object = objectId.AsOpenFgaIdTupleString(),
            },
            cancellationToken
        );

    /// <summary>
    ///     Lists objects that a user has a specific relation to, returning strongly typed IDs.
    /// </summary>
    /// <param name="user">The user to check (strongly typed)</param>
    /// <param name="relation">The relation to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <returns>List of strongly typed object IDs</returns>
    public async Task<IEnumerable<TObjId>> ListObjectsAsync<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        CancellationToken cancellationToken = new()
    )
        where TObjId : ISealedFgaTypeId<TObjId> {
        var objectStrings = await ListObjectsAsync(
            user.AsOpenFgaIdTupleString(),
            relation.AsOpenFgaString(),
            IdUtil.GetNameByIdType(typeof(TObjId)),
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

    /// <summary>
    ///     Performs batch check operations using strongly typed IDs.
    /// </summary>
    /// <param name="checks">List of check requests with strongly typed IDs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TObjId">The object ID type</typeparam>
    /// <returns>Dictionary with results for each check</returns>
    public async Task<Dictionary<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object), bool>>
        BatchCheckAsync<TObjId>(
            IEnumerable<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object)> checks,
            CancellationToken cancellationToken = new()
        )
        where TObjId : ISealedFgaTypeId<TObjId> {
        var checksAsList = checks.ToList();
        var results = await BatchCheckAsync(
            checksAsList.Select(check => new TupleKey {
                    User = check.User.AsOpenFgaIdTupleString(),
                    Relation = check.Relation.AsOpenFgaString(),
                    Object = check.Object.AsOpenFgaIdTupleString(),
                }
            ),
            cancellationToken
        );

        return checksAsList.ToDictionary(
            check => (check.User, check.Relation, check.Object),
            check => results.ElementAt(checksAsList.IndexOf(check))
        );
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

        var response = await openFgaClient.Read(request, cancellationToken: cancellationToken);
        return response.Tuples;
    }

    /// <summary>
    ///     Checks authorization using raw strings.
    /// </summary>
    /// <param name="tuple">The tuple to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the relation exists, false otherwise</returns>
    internal async Task<bool> CheckAsync(
        TupleKey tuple,
        CancellationToken cancellationToken = new()
    ) {
        var response = await openFgaClient.Check(new ClientCheckRequest {
                User = tuple.User,
                Relation = tuple.Relation,
                Object = tuple.Object,
            },
            cancellationToken: cancellationToken
        );

        return response.Allowed ?? false;
    }

    /// <summary>
    ///     Modifies all relations that include a reference to the specified old raw ID and updates them to reference the new
    ///     raw ID.
    /// </summary>
    /// <param name="rawOldId">The current raw identifier to be updated.</param>
    /// <param name="rawNewId">The new raw identifier to replace the old one.</param>
    /// <param name="typeName">The name of the type associated with the ID for contextual identification.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task ModifyIdAsync(
        string rawOldId,
        string rawNewId,
        string typeName,
        CancellationToken cancellationToken = new()
    ) {
        // Find stored relations TO the old ID and FROM the old ID, de-duplicated (a self-referential
        // tuple would otherwise appear in both lists).
        var oldRelationTuples = await ListAllRelationsToObjectAsync(
            rawOldId,
            cancellationToken
        );
        oldRelationTuples.AddRange(
            await ListAllRelationsFromUserAsync(
                rawOldId,
                typeName,
                cancellationToken
            )
        );
        oldRelationTuples = DistinctTuples(oldRelationTuples);

        // Build the replacement tuples, rewriting only exact ID segments (never substrings).
        var newRelationTuples = oldRelationTuples.Select(tuple => new TupleKey {
                User = ReplaceIdSegment(tuple.User, rawOldId, rawNewId),
                Relation = tuple.Relation,
                Object = ReplaceIdSegment(tuple.Object, rawOldId, rawNewId),
            }
        ).ToList();

        // Idempotent write of the new tuples + delete of the old ones, in a single OpenFGA transaction.
        await SafeWriteAndDeleteTuplesAsync(newRelationTuples, oldRelationTuples, cancellationToken);
    }

    /// <summary>
    ///     Lists objects that a user has a specific relation to using raw strings.
    /// </summary>
    /// <param name="rawUser">The user string</param>
    /// <param name="rawRelation">The relation string</param>
    /// <param name="objectTypeName">The type of objects to list</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of object strings</returns>
    internal async Task<IEnumerable<string>> ListObjectsAsync(
        string rawUser,
        string rawRelation,
        string objectTypeName,
        CancellationToken cancellationToken = new()
    ) {
        var response = await openFgaClient.ListObjects(new ClientListObjectsRequest {
                User = rawUser,
                Relation = rawRelation,
                Type = objectTypeName,
            },
            cancellationToken: cancellationToken
        );

        return response.Objects;
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

        var response = await openFgaClient.Read(readRequest, cancellationToken: cancellationToken);
        return response.Tuples.Select(t => t.Key).ToList();
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
        // Determine which object types can reference our type as a directly related user.
        var authModel = await openFgaClient.ReadAuthorizationModel(cancellationToken: cancellationToken);
        var typeDefinitions = authModel.AuthorizationModel!.TypeDefinitions;

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
            var response = await openFgaClient.Read(
                new ClientReadRequest {
                    User = rawUser,
                    Object = $"{typeDef.Type}:",
                },
                cancellationToken: cancellationToken
            );
            relationTuples.AddRange(response.Tuples.Select(t => t.Key));
        }

        return relationTuples;
    }

    /// <summary>
    ///     Performs batch check operations using raw strings.
    /// </summary>
    /// <param name="checks">List of check requests with raw strings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Results in the same order as input checks</returns>
    internal async Task<IEnumerable<bool>> BatchCheckAsync(
        IEnumerable<TupleKey> checks,
        CancellationToken cancellationToken = new()
    ) {
        // TODO: OpenFGA .NET SDK does not support batch check operations directly. Switch to them when available.
        var checkTasks = checks.Select(async check => {
                try {
                    return await CheckAsync(check, cancellationToken);
                } catch (Exception) {
                    return false;
                }
            }
        );

        return await Task.WhenAll(checkTasks);
    }

    #endregion

    #region Write/Delete Methods

    /// <summary>
    ///     Safely writes a list of tuples to OpenFGA after checking if they don't already exist.
    ///     This prevents failures when attempting to write tuples that already exist.
    /// </summary>
    /// <param name="tuples">The list of tuples to write</param>
    /// <param name="ct">The cancellation token to cancel the operation if needed</param>
    /// <returns>
    ///     A task that represents the asynchronous operation.
    /// </returns>
    public async Task SafeWriteTupleAsync(
        List<TupleKey> tuples,
        CancellationToken ct = new()
    ) => await SafeWriteAndDeleteTuplesAsync( // No delete tuples for write operation
        tuples,
        [],
        ct
    );

    /// <summary>
    ///     Safely deletes a list of tuples from OpenFGA after checking if they exist.
    ///     This prevents failures when attempting to delete tuples that don't exist.
    /// </summary>
    /// <param name="tuples">The list of tuples to delete</param>
    /// <param name="ct">The cancellation token to cancel the operation if needed</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SafeDeleteTupleAsync(List<TupleKey> tuples, CancellationToken ct = new())
        => await SafeWriteAndDeleteTuplesAsync([], // No write tuples for delete operation
            tuples,
            ct
        );

    /// <summary>
    ///     Executes a safe operation for tuple deletion and writing in batches, ensuring that
    ///     only necessary tuples are processed based on the results of batch checks.
    /// </summary>
    /// <param name="writeTuples">A list of tuples to be checked and potentially written.</param>
    /// <param name="deleteTuples">A list of tuples to be checked and potentially deleted.</param>
    /// <param name="ct">An optional cancellation token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task SafeWriteAndDeleteTuplesAsync(
        List<TupleKey> writeTuples,
        List<TupleKey> deleteTuples,
        CancellationToken ct = new()
    ) {
        // Check all tuples to avoid conflicts
        var deleteChecks = await BatchCheckAsync(
            deleteTuples,
            ct
        );
        var writeChecks = await BatchCheckAsync(
            writeTuples,
            ct
        );

        // Only process tuples that need to be deleted or written
        var deleteRequests = deleteTuples
                            .Where((_, index) => deleteChecks.ElementAt(index))
                            .Select(tuple => new ClientTupleKeyWithoutCondition {
                                     User = tuple.User,
                                     Relation = tuple.Relation,
                                     Object = tuple.Object,
                                 }
                             )
                            .ToList();
        var writeRequests = writeTuples
                           .Where((_, index) => !writeChecks.ElementAt(index))
                           .Select(tuple => new ClientTupleKey {
                                    User = tuple.User,
                                    Relation = tuple.Relation,
                                    Object = tuple.Object,
                                }
                            )
                           .ToList();

        // Execute requests
        if (deleteRequests.Count > 0 || writeRequests.Count > 0) {
            var writeRequest = new ClientWriteRequest {
                Deletes = deleteRequests,
                Writes = writeRequests,
            };

            await openFgaClient.Write(writeRequest, cancellationToken: ct);
        }
    }

    #endregion Write/Delete Methods

    #region Helpers

    /// <summary>
    ///     Replaces an exact OpenFGA ID segment. A tuple field is either exactly the ID
    ///     (<c>type:id</c>) or a userset (<c>type:id#relation</c>); anything else is left untouched so
    ///     that IDs which happen to be substrings of other IDs cannot be corrupted.
    /// </summary>
    internal static string ReplaceIdSegment(string field, string oldId, string newId) {
        if (field == oldId) {
            return newId;
        }

        // Userset subject form: "type:id#relation".
        if (field.StartsWith(oldId + "#", StringComparison.Ordinal)) {
            return newId + field.Substring(oldId.Length);
        }

        return field;
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
