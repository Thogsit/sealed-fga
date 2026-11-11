using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenFga.Language.Model;
using SealedFga.Generators.AuthModel;
using SealedFga.Models;
using SealedFga.Util;

namespace SealedFga.Generators.Fga;

/// <summary>
///     Emits <c>SealedFgaCheckDispatch.TryCheckByTypeNameAsync</c>: a generated map from an OpenFGA type
///     name (a raw string, e.g. taken from an HTTP request) to the strongly-typed
///     <see cref="SealedFga.Fga.ISealedFgaService" /> check for that type. Generating it from every
///     <c>[SealedFgaTypeId]</c> removes the need for a hand-maintained per-type <c>switch</c> at the call
///     site — the mapping cannot drift out of sync or silently miss a type.
/// </summary>
internal static class SealedFgaCheckDispatchGenerator {
    public static GeneratedFile Generate(
        AuthorizationModel authModel,
        ImmutableArray<IdClassToGenerateData> idClasses,
        bool splitRelationClasses
    ) => new(
        "SealedFgaCheckDispatch.g.cs",
        $$"""
          /// <summary>
          ///     Generated dispatcher for the "check any permission on any object type" surface. Maps an
          ///     OpenFGA type name to the strongly-typed <see cref="ISealedFgaService.CheckAsync{TObjId}" />
          ///     call for that type, so callers with a stringly-typed request do not hand-maintain a switch.
          /// </summary>
          public static class SealedFgaCheckDispatch {
              /// <summary>
              ///     Checks <paramref name="relation" /> for <paramref name="user" /> against the object
              ///     <c>{typeName}:{objectId}</c>, dispatching on <paramref name="typeName" /> to the
              ///     strongly-typed check for that type. The typed call path is preserved (not a raw tuple
              ///     string) so any ambient options provider — e.g. a super-user contextual tuple — still
              ///     applies. Returns <c>null</c> when <paramref name="typeName" /> is not a known checkable
              ///     SealedFGA type; the caller typically maps that to a 400.
              /// </summary>
              /// <param name="svc">The SealedFGA service to check against.</param>
              /// <param name="user">The user/subject being checked.</param>
              /// <param name="typeName">The object's OpenFGA type name (e.g. <c>"charger"</c>).</param>
              /// <param name="relation">The relation/permission to check (e.g. <c>"can_view"</c>).</param>
              /// <param name="objectId">The object's raw ID (parsed into the type's strongly-typed ID).</param>
              /// <param name="queryOptions">Optional per-call options (contextual tuples, consistency).</param>
              /// <param name="cancellationToken">Cancellation token.</param>
              /// <returns><c>true</c>/<c>false</c> for the check, or <c>null</c> for an unknown type name.</returns>
              public static async Task<bool?> TryCheckByTypeNameAsync(
                  this ISealedFgaService svc,
                  ISealedFgaUser user,
                  string typeName,
                  string relation,
                  string objectId,
                  SealedFgaQueryOptions? queryOptions = null,
                  CancellationToken cancellationToken = default
              ) => typeName switch {
                      {{GetSwitchArms(authModel, idClasses, splitRelationClasses)}}
                      _ => null,
                  };

              /// <summary>
              ///     Checks a batch in input order with one typed BatchCheckAsync call per object type.
              ///     Returns null without service calls if any type is unknown or not checkable.
              ///     Empty input returns an empty list. Like TryCheckByTypeNameAsync, invalid IDs throw
              ///     from the generated Parse helper and arbitrary relation strings pass through to the service.
              ///     Explicit options and per-object ambient contextual tuples apply through the typed batch API.
              /// </summary>
              public static async Task<IReadOnlyList<bool>?> TryBatchCheckByTypeNameAsync(
                  this ISealedFgaService svc,
                  ISealedFgaUser user,
                  IReadOnlyList<TypeNameCheck> checks,
                  SealedFgaQueryOptions? options = null,
                  CancellationToken ct = default
              ) {
                  var groups = new Dictionary<string, List<(int Index, TypeNameCheck Check)>>();
                  for (var i = 0; i < checks.Count; i++) {
                      var check = checks[i];
                      if (!(check.TypeName switch {
                          {{GetKnownTypeArms(authModel, idClasses, splitRelationClasses)}}
                          _ => false,
                      })) return null;
                      if (!groups.TryGetValue(check.TypeName, out var group)) {
                          group = new List<(int Index, TypeNameCheck Check)>();
                          groups.Add(check.TypeName, group);
                      }
                      group.Add((i, check));
                  }

                  var results = new bool[checks.Count];
                  foreach (var group in groups) {
                      switch (group.Key) {
                          {{GetBatchCases(authModel, idClasses, splitRelationClasses)}}
                      }
                  }
                  return results;
              }

              private static async Task BatchCheckTypeAsync<TObjId>(
                  ISealedFgaService svc,
                  ISealedFgaUser user,
                  List<(int Index, TypeNameCheck Check)> group,
                  Func<string, ISealedFgaRelation<TObjId>> relationFromString,
                  Func<string, TObjId> parseId,
                  bool[] results,
                  SealedFgaQueryOptions? options,
                  CancellationToken ct
              ) where TObjId : ISealedFgaTypeId<TObjId> {
                  var typedChecks = new List<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object)>();
                  foreach (var item in group) {
                      typedChecks.Add((user, relationFromString(item.Check.Relation), parseId(item.Check.ObjectId)));
                  }
                  var answers = await svc.BatchCheckAsync(typedChecks, options, ct);
                  for (var i = 0; i < group.Count; i++) {
                      results[group[i].Index] = answers[typedChecks[i]];
                  }
              }
          }
          """,
        new HashSet<string>([
                "System",
                "System.Collections.Generic",
                "System.Threading",
                "System.Threading.Tasks",
                Settings.AuthModelNamespace,
                Settings.FgaNamespace,
            ]
        )
    );

    /// <summary>
    ///     One <c>"typeName" =&gt; await svc.CheckAsync(...)</c> arm per checkable <c>[SealedFgaTypeId]</c>.
    ///     Types with no generated relation class are not checkable and are omitted (they fall through to
    ///     the <c>_ =&gt; null</c> default). Arms are ordered by type name and de-duplicated so the emitted
    ///     source is deterministic and never contains a duplicate <c>case</c> label.
    /// </summary>
    private static string GetSwitchArms(
        AuthorizationModel authModel,
        ImmutableArray<IdClassToGenerateData> idClasses,
        bool splitRelationClasses
    ) {
        var arms = GetDispatchTypes(authModel, idClasses, splitRelationClasses)
            .Select(type => $"\"{type.TypeName}\" => await svc.CheckAsync(user, "
                          + $"{type.RelationClass}.FromOpenFgaString(relation), {type.IdClass}.Parse(objectId), "
                          + "queryOptions, cancellationToken),");

        // 12 = the emitted indent of the switch arms (matches the `{{...}}` placeholder column), so
        // multi-arm output stays aligned with the first arm.
        return GeneratorUtil.BuildLinesWithIndent(arms, 12);
    }

    private static string GetKnownTypeArms(
        AuthorizationModel authModel,
        ImmutableArray<IdClassToGenerateData> idClasses,
        bool splitRelationClasses
    ) => GeneratorUtil.BuildLinesWithIndent(
        GetDispatchTypes(authModel, idClasses, splitRelationClasses)
            .Select(type => $"\"{type.TypeName}\" => true,")
            .DefaultIfEmpty("// No checkable types."), 16);

    private static string GetBatchCases(
        AuthorizationModel authModel,
        ImmutableArray<IdClassToGenerateData> idClasses,
        bool splitRelationClasses
    ) => GeneratorUtil.BuildLinesWithIndent(
        GetDispatchTypes(authModel, idClasses, splitRelationClasses)
            .SelectMany(type => new[] {
                $"case \"{type.TypeName}\":",
                $"    await BatchCheckTypeAsync<{type.IdClass}>(svc, user, group.Value, "
                + $"{type.RelationClass}.FromOpenFgaString, {type.IdClass}.Parse, results, options, ct);",
                "    break;",
            }).DefaultIfEmpty("// No checkable types."), 16);

    private static IEnumerable<(string TypeName, string IdClass, string RelationClass)> GetDispatchTypes(
        AuthorizationModel authModel,
        ImmutableArray<IdClassToGenerateData> idClasses,
        bool splitRelationClasses
    ) {
        var seenTypeNames = new HashSet<string>();
        foreach (var idClass in idClasses.OrderBy(id => id.TypeName, System.StringComparer.Ordinal)) {
            var relationClass = TypeNameRelationsGenerator.ResolveDispatchRelationClassName(
                authModel, idClass, splitRelationClasses);
            if (relationClass is null || !seenTypeNames.Add(idClass.TypeName))
                continue;
            yield return (idClass.TypeName, $"{idClass.ClassNamespace}.{idClass.ClassName}",
                $"{idClass.ClassNamespace}.{relationClass}");
        }
    }
}
