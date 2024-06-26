﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using Internal.TypeSystem;

namespace Microsoft.Diagnostics.Tools.Pgo.TypeRefTypeSystem
{
    class TypeRefTypeSystemModule : ModuleDesc, IAssemblyDesc
    {
        AssemblyNameInfo _name;
        List<TypeRefTypeSystemType> _types = new List<TypeRefTypeSystemType>();
        Dictionary<string, TypeRefTypeSystemType> _nonNamespacedTypes = new Dictionary<string, TypeRefTypeSystemType>();
        Dictionary<string, Dictionary<string, TypeRefTypeSystemType>> _namespacedTypes = new Dictionary<string, Dictionary<string, TypeRefTypeSystemType>>();

        public TypeRefTypeSystemModule(TypeSystemContext tsc, AssemblyNameInfo name) : base(tsc, null)
        {
            _name = name;
        }

        public TypeRefTypeSystemType GetOrAddType(string nameSpace, string name)
        {
            TypeRefTypeSystemType type = GetTypeInternal(nameSpace, name);
            if (type == null)
            {
                type = new TypeRefTypeSystemType(nameSpace, name, this);

                Dictionary<string, TypeRefTypeSystemType> nameToTypeDictionary = _nonNamespacedTypes;
                if (!String.IsNullOrEmpty(nameSpace))
                {
                    if (!_namespacedTypes.TryGetValue(nameSpace, out nameToTypeDictionary))
                    {
                        nameToTypeDictionary = new Dictionary<string, TypeRefTypeSystemType>();
                        _namespacedTypes.Add(nameSpace, nameToTypeDictionary);
                    }
                }

                nameToTypeDictionary.Add(name, type);
                _types.Add(type);
            }

            return type;
        }

        public override IAssemblyDesc Assembly => this;

        public override IEnumerable<MetadataType> GetAllTypes() => _types;
        public override MetadataType GetGlobalModuleType() => throw new NotImplementedException();
        public AssemblyNameInfo GetName() => _name;
        private TypeRefTypeSystemType GetTypeInternal(string nameSpace, string name)
        {
            Dictionary<string, TypeRefTypeSystemType> nameToTypeDictionary = _nonNamespacedTypes;
            if (!string.IsNullOrEmpty(nameSpace))
            {
                if (!_namespacedTypes.TryGetValue(nameSpace, out nameToTypeDictionary))
                {
                    return null;
                }
            }

            if (!nameToTypeDictionary.TryGetValue(name, out TypeRefTypeSystemType type))
            {
                return null;
            }

            return type;
        }

        public override object GetType(string nameSpace, string name, NotFoundBehavior notFoundBehavior)
        {
            MetadataType type = GetTypeInternal(nameSpace, name);
            if ((type == null) && notFoundBehavior != NotFoundBehavior.ReturnNull)
            {
                ResolutionFailure failure = ResolutionFailure.GetTypeLoadResolutionFailure(nameSpace, name, this);
                if (notFoundBehavior == NotFoundBehavior.Throw)
                    failure.Throw();
                return failure;
            }
            return type;
        }
    }
}
