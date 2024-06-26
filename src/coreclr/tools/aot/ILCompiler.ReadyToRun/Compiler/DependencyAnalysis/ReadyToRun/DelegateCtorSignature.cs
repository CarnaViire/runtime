// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Internal.TypeSystem;
using Internal.JitInterface;
using Internal.Text;
using Internal.ReadyToRunConstants;

namespace ILCompiler.DependencyAnalysis.ReadyToRun
{
    public class DelegateCtorSignature : Signature
    {
        private readonly TypeDesc _delegateType;

        private readonly IMethodNode _targetMethod;

        private readonly MethodWithToken _methodToken;

        public DelegateCtorSignature(
            TypeDesc delegateType,
            IMethodNode targetMethod,
            MethodWithToken methodToken)
        {
            _delegateType = delegateType;
            _targetMethod = targetMethod;
            _methodToken = methodToken;

            // Ensure types in signature are loadable and resolvable, otherwise we'll fail later while emitting the signature
            CompilerTypeSystemContext compilerContext = (CompilerTypeSystemContext)delegateType.Context;
            compilerContext.EnsureLoadableType(delegateType);
            compilerContext.EnsureLoadableMethod(targetMethod.Method);
        }

        public override int ClassCode => 99885741;

        public override ObjectData GetData(NodeFactory factory, bool relocsOnly = false)
        {
            ObjectDataSignatureBuilder builder = new ObjectDataSignatureBuilder();
            builder.AddSymbol(this);

            if (!relocsOnly)
            {
                SignatureContext innerContext = builder.EmitFixup(factory, ReadyToRunFixupKind.DelegateCtor, _methodToken.Token.Module, factory.SignatureContext);

                bool needsInstantiatingStub = _targetMethod.Method.HasInstantiation;
                if (_targetMethod.Method.IsVirtual && _targetMethod.Method.Signature.IsStatic)
                {
                    // For static virtual methods, we always require an instantiating stub as the method may resolve to a canonical representation
                    // at runtime without us being able to detect that at compile time.
                    needsInstantiatingStub |= (_targetMethod.Method.OwningType.HasInstantiation || _methodToken.ConstrainedType != null);
                }

                builder.EmitMethodSignature(
                    _methodToken,
                    enforceDefEncoding: false,
                    enforceOwningType: false,
                    innerContext,
                    isInstantiatingStub: needsInstantiatingStub);

                builder.EmitTypeSignature(_delegateType, innerContext);
            }

            return builder.ToObjectData();
        }

        protected override DependencyList ComputeNonRelocationBasedDependencies(NodeFactory factory)
        {
            return new DependencyList(
                new DependencyListEntry[]
                {
                    new DependencyListEntry(_targetMethod, "Delegate target method")
                }
            );
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append(nameMangler.CompilationUnitPrefix);
            sb.Append($@"DelegateCtor(");
            sb.Append(nameMangler.GetMangledTypeName(_delegateType));
            sb.Append(" -> "u8);
            _targetMethod.AppendMangledName(nameMangler, sb);
            sb.Append("; "u8);
            sb.Append(_methodToken.ToString());
            sb.Append(")"u8);
        }

        public override int CompareToImpl(ISortableNode other, CompilerComparer comparer)
        {
            DelegateCtorSignature otherNode = (DelegateCtorSignature)other;
            int result = comparer.Compare(_delegateType, otherNode._delegateType);
            if (result != 0)
                return result;

            result = comparer.Compare(_targetMethod, otherNode._targetMethod);
            if (result != 0)
                return result;

            return _methodToken.CompareTo(otherNode._methodToken, comparer);
        }
    }
}
