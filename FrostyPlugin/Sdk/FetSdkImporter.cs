using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FrostySdk;
using FrostySdk.IO;
using Mono.Cecil;

namespace Frosty.Core.Sdk
{
    // Converts a FET (FIFA Editor Tool, net9) generated FC26SDK.dll into a Frosty-format
    // SDK assembly. FET cannot be loaded via reflection from .NET Framework, so we read its
    // metadata with Mono.Cecil (framework-agnostic) and re-emit the type definitions through
    // Frosty's existing ModuleWriter (which compiles a Frosty-compatible SDK).
    //
    // The two tools share identical Ebx* attribute shapes, so the mapping is a direct copy:
    //   EbxClassMeta(flags, alignment, size, namespace)
    //   EbxFieldMeta(flags, offset, typeof(baseType), isArray, arrayFlags)
    //   TypeInfoGuid(guid)  /  Hash(nameHash)  /  FieldIndex(index)
    public static class FetSdkImporter
    {
        public static bool Import(string fetDllPath, out string error)
        {
            error = null;
            var log = new StringBuilder();
            try
            {
                if (!File.Exists(fetDllPath))
                {
                    error = "FET SDK DLL not found: " + fetDllPath;
                    return false;
                }

                AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(fetDllPath);
                DbObject classList = DbObject.CreateList();

                int classes = 0, enums = 0, skipped = 0;
                foreach (TypeDefinition type in asm.MainModule.GetTypes())
                {
                    // skip compiler-generated / non-public helper types and the module type
                    if (type.Name == "<Module>" || type.Name.StartsWith("<"))
                        continue;

                    CustomAttribute classMeta = FindAttr(type.CustomAttributes, "EbxClassMetaAttribute");
                    if (classMeta == null)
                    {
                        skipped++;
                        continue;
                    }

                    DbObject classObj = type.IsEnum
                        ? BuildEnum(type, classMeta)
                        : BuildClass(type, classMeta);

                    if (classObj == null) { skipped++; continue; }

                    classList.Add(classObj);
                    if (type.IsEnum) enums++; else classes++;
                }

                log.AppendLine($"[FET SDK] classes={classes} enums={enums} skipped={skipped}");

                if (classList.Count == 0)
                {
                    error = "No EbxClassMeta types found in the FET SDK — wrong DLL?";
                    File.WriteAllText("fet_sdk_import.txt", log.ToString());
                    return false;
                }

                Directory.CreateDirectory("TmpProfiles");
                string outPath = "TmpProfiles\\" + ProfilesLibrary.SDKFilename + ".dll";
                using (ModuleWriter writer = new ModuleWriter(outPath, classList))
                    writer.Write(App.FileSystem.Head);

                log.AppendLine("[FET SDK] wrote " + outPath + " (version=" + App.FileSystem.Head + ")");
                File.WriteAllText("fet_sdk_import.txt", log.ToString());
                return File.Exists(outPath);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                log.AppendLine("[FET SDK] EXCEPTION " + ex);
                try { File.WriteAllText("fet_sdk_import.txt", log.ToString()); } catch { }
                return false;
            }
        }

        private static DbObject BuildClass(TypeDefinition type, CustomAttribute classMeta)
        {
            int flags     = ToInt(classMeta.ConstructorArguments[0].Value);
            int alignment = ToInt(classMeta.ConstructorArguments[1].Value);
            int size      = ToInt(classMeta.ConstructorArguments[2].Value);
            string ns     = classMeta.ConstructorArguments[3].Value as string ?? "";

            DbObject obj = DbObject.CreateObject();
            obj.SetValue("name", Clean(type.Name));
            obj.SetValue("type", (flags >> 4) & 0x1F);
            obj.SetValue("flags", flags);
            obj.SetValue("alignment", alignment);
            obj.SetValue("size", size);
            obj.SetValue("namespace", ns);

            // parent: only real base classes (root types inherit object → no parent)
            if (type.BaseType != null && !IsRootBase(type.BaseType.Name))
                obj.SetValue("parent", Clean(type.BaseType.Name));

            // type info guids (runtime registration guids used by the EBX reader)
            DbObject guidList = DbObject.CreateList();
            foreach (CustomAttribute g in type.CustomAttributes.Where(a => a.AttributeType.Name == "TypeInfoGuidAttribute"))
            {
                if (Guid.TryParse(g.ConstructorArguments[0].Value as string, out Guid tg))
                    guidList.Add(tg);
            }
            if (guidList.Count > 0)
                obj.SetValue("typeInfoGuid", guidList);

            CustomAttribute guidAttr = FindAttr(type.CustomAttributes, "GuidAttribute");
            if (guidAttr != null && Guid.TryParse(guidAttr.ConstructorArguments[0].Value as string, out Guid cg))
                obj.SetValue("guid", cg);

            // fields
            DbObject fields = DbObject.CreateList();
            int index = 0;
            foreach (PropertyDefinition prop in type.Properties)
            {
                CustomAttribute fieldMeta = FindAttr(prop.CustomAttributes, "EbxFieldMetaAttribute");
                if (fieldMeta == null || fieldMeta.ConstructorArguments.Count < 5)
                    continue;

                int fFlags      = ToInt(fieldMeta.ConstructorArguments[0].Value);
                int fOffset     = ToInt(fieldMeta.ConstructorArguments[1].Value);
                object baseTypeV = fieldMeta.ConstructorArguments[2].Value; // TypeReference or null
                bool isArray    = Convert.ToBoolean(fieldMeta.ConstructorArguments[3].Value);
                int arrayFlags  = ToInt(fieldMeta.ConstructorArguments[4].Value);

                int fType = (fFlags >> 4) & 0x1F;

                DbObject f = DbObject.CreateObject();
                f.SetValue("name", prop.Name);
                f.SetValue("type", fType);
                f.SetValue("flags", fFlags);
                f.SetValue("offset", fOffset);
                f.SetValue("arrayFlags", arrayFlags);
                f.SetValue("index", index++);

                // baseType is required for Struct/Enum (it IS the field type) and used in the
                // attribute for Pointer/Array. FET puts the target in EbxFieldMeta.BaseType for
                // pointers/arrays, but for structs/enums the type is the property's own type and
                // the attribute arg is null — so fall back to the declared property type.
                if (fType == 2 /*Struct*/ || fType == 3 /*Pointer*/ || fType == 8 /*Enum*/ || fType == 4 /*Array*/)
                {
                    string baseTypeName = null;
                    if (baseTypeV is TypeReference btr)
                        baseTypeName = btr.Name;
                    else
                    {
                        TypeReference pt = prop.PropertyType;
                        if (pt is GenericInstanceType git && git.GenericArguments.Count >= 1)
                            pt = git.GenericArguments[git.GenericArguments.Count - 1]; // List<T> element
                        if (pt != null)
                            baseTypeName = pt.Name;
                    }
                    if (!string.IsNullOrEmpty(baseTypeName) && baseTypeName != "PointerRef")
                        f.SetValue("baseType", Clean(baseTypeName));
                }

                CustomAttribute hash = FindAttr(prop.CustomAttributes, "HashAttribute");
                if (hash != null)
                    f.SetValue("nameHash", ToInt(hash.ConstructorArguments[0].Value));

                CustomAttribute fidx = FindAttr(prop.CustomAttributes, "FieldIndexAttribute");
                if (fidx != null)
                    f.SetValue("index", ToInt(fidx.ConstructorArguments[0].Value));

                fields.Add(f);
            }
            obj.SetValue("fields", fields);
            return obj;
        }

        private static DbObject BuildEnum(TypeDefinition type, CustomAttribute classMeta)
        {
            int flags     = ToInt(classMeta.ConstructorArguments[0].Value);
            int alignment = ToInt(classMeta.ConstructorArguments[1].Value);
            int size      = ToInt(classMeta.ConstructorArguments[2].Value);
            string ns     = classMeta.ConstructorArguments[3].Value as string ?? "";

            DbObject obj = DbObject.CreateObject();
            obj.SetValue("name", Clean(type.Name));
            obj.SetValue("type", 8 /* Enum */);
            obj.SetValue("flags", flags);
            obj.SetValue("alignment", alignment);
            obj.SetValue("size", size);
            obj.SetValue("namespace", ns);

            DbObject guidList = DbObject.CreateList();
            foreach (CustomAttribute g in type.CustomAttributes.Where(a => a.AttributeType.Name == "TypeInfoGuidAttribute"))
            {
                if (Guid.TryParse(g.ConstructorArguments[0].Value as string, out Guid tg))
                    guidList.Add(tg);
            }
            if (guidList.Count > 0)
                obj.SetValue("typeInfoGuid", guidList);

            DbObject fields = DbObject.CreateList();
            foreach (FieldDefinition fd in type.Fields)
            {
                if (fd.Name == "value__" || !fd.HasConstant)
                    continue;
                DbObject f = DbObject.CreateObject();
                f.SetValue("name", fd.Name);
                f.SetValue("value", ToInt(fd.Constant));
                fields.Add(f);
            }
            obj.SetValue("fields", fields);
            return obj;
        }

        private static CustomAttribute FindAttr(Mono.Collections.Generic.Collection<CustomAttribute> attrs, string name)
            => attrs.FirstOrDefault(a => a.AttributeType.Name == name);

        private static int ToInt(object v) => v == null ? 0 : Convert.ToInt32(v);

        private static string Clean(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            int tick = n.IndexOf('`');           // strip generic arity suffix (e.g. List`1)
            if (tick >= 0) n = n.Substring(0, tick);
            return n.Replace(':', '_').Replace('/', '_');
        }

        private static bool IsRootBase(string baseName)
            => baseName == "Object" || baseName == "ValueType" || baseName == "Enum" || baseName == "Attribute";
    }
}
