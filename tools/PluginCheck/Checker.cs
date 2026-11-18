using Mono.Cecil;

namespace Sleezer.PluginCheck
{
    public sealed class Checker
    {
        private readonly ModuleDefinition _module;
        private readonly string _self;
        private readonly SortedSet<string> _problems = new(StringComparer.Ordinal);

        public Checker(string plugin, string lib)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(lib);
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(plugin)));
            resolver.AddSearchDirectory(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            _module = ModuleDefinition.ReadModule(plugin, new ReaderParameters { AssemblyResolver = resolver });
            _self = _module.Assembly.Name.Name;
        }

        public int TypeRefs { get; private set; }
        public int MemberRefs { get; private set; }
        public int RequiredMembers { get; private set; }

        public IReadOnlyCollection<string> Run()
        {
            CheckTypeRefs();
            CheckMemberRefs();
            CheckImplementations();
            return _problems;
        }

        private void CheckTypeRefs()
        {
            foreach (var tr in _module.GetTypeReferences().Where(t => IsLidarr(Outer(t).Scope)))
            {
                TypeRefs++;
                if (tr.Resolve() == null)
                {
                    _problems.Add($"missing type   {tr.FullName}  ({Outer(tr).Scope.Name})");
                }
            }
        }

        private void CheckMemberRefs()
        {
            foreach (var mr in _module.GetMemberReferences().Where(m => IsLidarr(Outer(Element(m.DeclaringType)).Scope)))
            {
                MemberRefs++;
                var resolved = mr switch { MethodReference m => (IMemberDefinition)m.Resolve(), FieldReference f => f.Resolve(), _ => null };
                if (resolved == null)
                {
                    _problems.Add($"missing member {mr.FullName}");
                }
            }
        }

        private void CheckImplementations()
        {
            foreach (var type in AllTypes(_module.Types).Where(t => !t.IsInterface && !t.IsAbstract))
            {
                foreach (var method in RequiredMethods(type))
                {
                    RequiredMembers++;
                    if (!Implements(type, method))
                    {
                        _problems.Add($"unimplemented  {type.FullName} lacks {method.FullName}");
                    }
                }
            }
        }

        private IEnumerable<MethodDefinition> RequiredMethods(TypeDefinition type)
        {
            var required = new List<TypeDefinition>();
            for (var b = type.BaseType; b != null && Element(b).Resolve() is TypeDefinition bd; b = bd.BaseType)
            {
                if (IsLidarr(Outer(Element(b)).Scope))
                {
                    required.Add(bd);
                }
            }

            CollectInterfaces(type, required);
            return required.SelectMany(t => t.Methods).Where(m => m.IsAbstract && !m.IsStatic).Distinct();
        }

        private void CollectInterfaces(TypeDefinition t, List<TypeDefinition> into)
        {
            if (t == null)
            {
                return;
            }

            foreach (var i in t.Interfaces)
            {
                var d = Element(i.InterfaceType).Resolve();
                if (d == null)
                {
                    continue;
                }

                if (IsLidarr(Outer(Element(i.InterfaceType)).Scope))
                {
                    into.Add(d);
                }

                CollectInterfaces(d, into);
            }

            CollectInterfaces(t.BaseType == null ? null : Element(t.BaseType).Resolve(), into);
        }

        private bool IsLidarr(IMetadataScope scope) =>
            scope is AssemblyNameReference a && a.Name.StartsWith("Lidarr.", StringComparison.Ordinal) && a.Name != _self;

        private static bool Implements(TypeDefinition type, MethodDefinition required)
        {
            for (var t = type; t != null; t = t.BaseType?.Resolve())
            {
                if (t.Methods.Any(m => !m.IsAbstract && (m.Name == required.Name || m.Name.EndsWith("." + required.Name, StringComparison.Ordinal)) && SameSignature(m, required)))
                {
                    return true;
                }

                if (t.Methods.Any(m => m.Overrides.Any(o => o.Name == required.Name && SameSignature(o, required))))
                {
                    return true;
                }
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

        private static TypeReference Outer(TypeReference t)
        {
            while (t.DeclaringType != null)
            {
                t = t.DeclaringType;
            }

            return t;
        }

        private static TypeReference Element(TypeReference t) => t is TypeSpecification s ? Element(s.ElementType) : t;
    }
}
