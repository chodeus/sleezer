using Mono.Cecil;

namespace Sleezer.PluginCheck
{
    public sealed class Checker
    {
        private readonly ModuleDefinition _module;
        private readonly string _self;
        private readonly string _lib;
        private readonly SortedSet<string> _problems = new(StringComparer.Ordinal);

        // The plugin's folder is not searched: a CI build keeps net8 Lidarr copies there that would hide an assembly the build lacks
        public Checker(string plugin, string lib)
        {
            _lib = lib;
            var resolver = new DefaultAssemblyResolver();
            resolver.RemoveSearchDirectory(".");
            resolver.RemoveSearchDirectory("bin");
            resolver.AddSearchDirectory(lib);
            _module = ModuleDefinition.ReadModule(plugin, new ReaderParameters { AssemblyResolver = resolver });
            _self = _module.Assembly.Name.Name;
        }

        public int TypeRefs { get; private set; }
        public int MemberRefs { get; private set; }
        public int RequiredMembers { get; private set; }
        public int Overrides { get; private set; }

        public IReadOnlyCollection<string> Run()
        {
            CheckTypeRefs();
            CheckMemberRefs();
            CheckImplementations();
            CheckOverrides();
            return _problems;
        }

        private void CheckTypeRefs()
        {
            foreach (var tr in _module.GetTypeReferences().Where(t => IsHost(t.Scope)))
            {
                TypeRefs++;
                if (ResolveType(tr) == null)
                {
                    _problems.Add($"missing type   {tr.FullName}  ({tr.Scope.Name})");
                }
            }
        }

        // Array members are made by the runtime, so they never resolve
        private void CheckMemberRefs()
        {
            foreach (var mr in _module.GetMemberReferences().Where(m => m.DeclaringType is not ArrayType && IsHost(m.DeclaringType.Scope)))
            {
                MemberRefs++;
                if (ResolveMember(mr) == null)
                {
                    _problems.Add($"missing member {mr.FullName}");
                }
            }
        }

        private void CheckImplementations()
        {
            foreach (var type in AllTypes(_module.Types).Where(t => !t.IsInterface && !t.IsAbstract))
            {
                var interfaces = new List<TypeDefinition>();
                CollectInterfaces(type, interfaces);
                foreach (var method in RequiredMethods(type, interfaces))
                {
                    RequiredMembers++;
                    if (!Implements(type, interfaces, method))
                    {
                        _problems.Add($"unimplemented  {type.FullName} lacks {method.FullName}");
                    }
                }
            }
        }

        // An override whose base member changed signature becomes a new member the host never calls; explicit overrides are member refs
        private void CheckOverrides()
        {
            foreach (var type in AllTypes(_module.Types).Where(t => !t.IsInterface))
            {
                foreach (var method in type.Methods.Where(m => m.IsVirtual && !m.IsNewSlot && !m.HasOverrides))
                {
                    Overrides++;
                    if (Overridden(type, method) == false)
                    {
                        _problems.Add($"not overriding {method.FullName}");
                    }
                }
            }
        }

        private IEnumerable<MethodDefinition> RequiredMethods(TypeDefinition type, List<TypeDefinition> interfaces)
        {
            var required = interfaces.Where(i => IsHost(i)).ToList();
            for (var b = ResolveType(type.BaseType); b != null; b = ResolveType(b.BaseType))
            {
                if (IsHost(b))
                {
                    required.Add(b);
                }
            }

            return required.SelectMany(t => t.Methods).Where(m => m.IsAbstract && !m.IsStatic).Distinct();
        }

        private static void CollectInterfaces(TypeDefinition t, List<TypeDefinition> into)
        {
            for (; t != null; t = ResolveType(t.BaseType))
            {
                foreach (var d in t.Interfaces.Select(i => ResolveType(i.InterfaceType)).Where(d => d != null && !into.Contains(d)))
                {
                    into.Add(d);
                    CollectInterfaces(d, into);
                }
            }
        }

        // Host: Lidarr's own assemblies, even ones the build dropped, and every library in its bin dir
        private bool IsHost(string assembly) =>
            assembly != _self && (assembly.StartsWith("Lidarr.", StringComparison.Ordinal) || File.Exists(Path.Combine(_lib, assembly + ".dll")));

        private bool IsHost(IMetadataScope scope) => scope is AssemblyNameReference a && IsHost(a.Name);

        private bool IsHost(TypeDefinition t) => IsHost(t.Module.Assembly.Name.Name);

        // An interface member is met by any virtual method of its name and signature or by an interface's default; a class member only by an override
        private static bool Implements(TypeDefinition type, List<TypeDefinition> interfaces, MethodDefinition required)
        {
            var isInterface = required.DeclaringType.IsInterface;
            for (var t = type; t != null; t = ResolveType(t.BaseType))
            {
                if (t.Methods.Any(m => m.IsVirtual && !m.IsAbstract && (isInterface || !m.IsNewSlot) && m.Name == required.Name && SameSignature(m, required)))
                {
                    return true;
                }

                if (t.Methods.Any(m => ExplicitlyOverrides(m, required)))
                {
                    return true;
                }
            }

            return isInterface && interfaces.Any(i => i.Methods.Any(m => !m.IsAbstract && ExplicitlyOverrides(m, required)));
        }

        private static bool ExplicitlyOverrides(MethodDefinition m, MethodDefinition required) =>
            m.Overrides.Any(o => o.Name == required.Name && o.DeclaringType.GetElementType().FullName == required.DeclaringType.FullName && SameSignature(o, required));

        // null when a base can't be read; a plugin type's own base the build lacks is reported by CheckTypeRefs
        private static bool? Overridden(TypeDefinition type, MethodDefinition method)
        {
            for (var b = type.BaseType; b != null;)
            {
                var bd = ResolveType(b);
                if (bd == null)
                {
                    return null;
                }

                if (bd.Methods.Any(m => m.IsVirtual && m.Name == method.Name && SameSignature(m, method)))
                {
                    return true;
                }

                b = bd.BaseType;
            }

            return false;
        }

        private static bool SameSignature(MethodReference m, MethodReference required) =>
            m.Parameters.Count == required.Parameters.Count
            && SameType(m.ReturnType, required.ReturnType)
            && m.Parameters.Zip(required.Parameters).All(p => SameType(p.First.ParameterType, p.Second.ParameterType));

        // A generic parameter on either side matches any type: substituting them needs every base's arguments
        private static bool SameType(TypeReference a, TypeReference b)
        {
            a = StripModifiers(a);
            b = StripModifiers(b);

            if (a is GenericParameter || b is GenericParameter)
            {
                return true;
            }

            if (a is GenericInstanceType ga && b is GenericInstanceType gb)
            {
                return SameType(ga.ElementType, gb.ElementType)
                    && ga.GenericArguments.Count == gb.GenericArguments.Count
                    && ga.GenericArguments.Zip(gb.GenericArguments).All(p => SameType(p.First, p.Second));
            }

            if (a is TypeSpecification sa && b is TypeSpecification sb)
            {
                return sa.GetType() == sb.GetType() && (sa as ArrayType)?.Rank == (sb as ArrayType)?.Rank && SameType(sa.ElementType, sb.ElementType);
            }

            return a.FullName == b.FullName;
        }

        private static TypeReference StripModifiers(TypeReference t) => t is IModifierType m ? StripModifiers(m.ElementType) : t;

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));

        // Cecil throws, rather than returning null, when the assembly itself is missing
        private static TypeDefinition ResolveType(TypeReference t)
        {
            try
            {
                return t?.GetElementType().Resolve();
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }
        }

        private static IMemberDefinition ResolveMember(MemberReference m)
        {
            try
            {
                return m switch { MethodReference r => r.Resolve(), FieldReference f => f.Resolve(), _ => null };
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }
        }
    }
}
