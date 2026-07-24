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
          }
          """,
        new HashSet<string>([
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
        var seenTypeNames = new HashSet<string>();
        var arms = idClasses
                  .OrderBy(idClass => idClass.TypeName, System.StringComparer.Ordinal)
                  .Select(idClass => {
                           var relationClassName = TypeNameRelationsGenerator.ResolveDispatchRelationClassName(
                               authModel,
                               idClass,
                               splitRelationClasses
                           );
                           if (relationClassName is null) return null; // not checkable → no arm
                           if (!seenTypeNames.Add(idClass.TypeName)) return null; // dedupe duplicate type names

                           var fqIdClass = $"{idClass.ClassNamespace}.{idClass.ClassName}";
                           var fqRelationClass = $"{idClass.ClassNamespace}.{relationClassName}";
                           return $"\"{idClass.TypeName}\" => await svc.CheckAsync(user, "
                                + $"{fqRelationClass}.FromOpenFgaString(relation), {fqIdClass}.Parse(objectId), "
                                + "queryOptions, cancellationToken),";
                       }
                   )
                  .Where(arm => arm is not null)
                  .Select(arm => arm!);

        // 12 = the emitted indent of the switch arms (matches the `{{...}}` placeholder column), so
        // multi-arm output stays aligned with the first arm.
        return GeneratorUtil.BuildLinesWithIndent(arms, 12);
    }
}
