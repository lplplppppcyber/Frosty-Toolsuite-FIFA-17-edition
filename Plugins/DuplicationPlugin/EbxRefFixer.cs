using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace DuplicationPlugin
{
    /// <summary>
    /// Walks every property of every EBX object and remaps external PointerRefs
    /// whose FileGuid exists in the provided oldToNew map.  Handles arbitrary
    /// asset types, not just ObjectBlueprint / MeshVariationDatabase.
    /// </summary>
    internal static class EbxRefFixer
    {
        private static readonly Type s_guidType   = typeof(Guid);
        private static readonly Type s_stringType = typeof(string);

        // Types whose values we never recurse into
        private static bool IsLeaf(Type t)
            => t.IsPrimitive || t.IsEnum || t == s_stringType || t == s_guidType
               || t.FullName == "FrostySdk.Ebx.CString"
               || t.FullName == "FrostySdk.Ebx.ResourceRef"
               || t.FullName == "FrostySdk.Ebx.FileRef"
               || t.FullName == "FrostySdk.Ebx.TypeRef";

        /// <summary>
        /// Scans and patches all PointerRefs inside <paramref name="asset"/>.
        /// Returns true if any refs were updated (caller should call Update() + ModifyEbx).
        /// </summary>
        public static bool Fix(EbxAsset asset, Dictionary<Guid, EbxAssetEntry> oldToNew)
        {
            bool modified = false;
            // Use identity-based visited set to break cycles between class instances.
            var visited = new HashSet<object>(ReferenceEqualityHelper.Instance);

            foreach (object obj in asset.Objects)
            {
                if (obj != null)
                    ScanObject(obj, oldToNew, ref modified, visited);
            }

            return modified;
        }

        private static void ScanObject(object obj, Dictionary<Guid, EbxAssetEntry> oldToNew,
            ref bool modified, HashSet<object> visited)
        {
            if (obj == null) return;

            Type t = obj.GetType();

            // Value types (structs) that are not PointerRef are either leaves or
            // handled at the property level below; we don't need cycle-guard them.
            bool isClass = t.IsClass;
            if (isClass && !visited.Add(obj))
                return;

            if (IsLeaf(t)) return;

            foreach (PropertyInfo prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0 || !prop.CanRead)
                    continue;

                object val;
                try { val = prop.GetValue(obj); }
                catch { continue; }

                if (val == null) continue;

                if (val is PointerRef pr)
                {
                    if (prop.CanWrite
                        && pr.Type == PointerRefType.External
                        && oldToNew.ContainsKey(pr.External.FileGuid))
                    {
                        EbxAsset target = App.AssetManager.GetEbx(oldToNew[pr.External.FileGuid]);
                        prop.SetValue(obj, new PointerRef(new EbxImportReference
                        {
                            FileGuid = target.FileGuid,
                            ClassGuid = pr.External.ClassGuid
                        }));
                        modified = true;
                    }
                }
                else if (val is IList list)
                {
                    ScanList(list, oldToNew, ref modified, visited);
                }
                else
                {
                    Type vt = val.GetType();
                    if (!IsLeaf(vt))
                        ScanObject(val, oldToNew, ref modified, visited);
                }
            }
        }

        private static void ScanList(IList list, Dictionary<Guid, EbxAssetEntry> oldToNew,
            ref bool modified, HashSet<object> visited)
        {
            for (int i = 0; i < list.Count; i++)
            {
                object item = list[i];
                if (item == null) continue;

                if (item is PointerRef pr)
                {
                    if (pr.Type == PointerRefType.External
                        && oldToNew.ContainsKey(pr.External.FileGuid))
                    {
                        EbxAsset target = App.AssetManager.GetEbx(oldToNew[pr.External.FileGuid]);
                        list[i] = new PointerRef(new EbxImportReference
                        {
                            FileGuid = target.FileGuid,
                            ClassGuid = pr.External.ClassGuid
                        });
                        modified = true;
                    }
                }
                else
                {
                    Type vt = item.GetType();
                    if (!IsLeaf(vt))
                        ScanObject(item, oldToNew, ref modified, visited);
                }
            }
        }

        // .NET Framework 4.8 lacks ReferenceEqualityComparer — provide our own.
        private sealed class ReferenceEqualityHelper : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityHelper Instance = new ReferenceEqualityHelper();
            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);
            int  IEqualityComparer<object>.GetHashCode(object obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
