﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;

using Internal.IL;
using Internal.Text;
using Internal.TypeSystem;

using ILCompiler.DependencyAnalysisFramework;

using FatFunctionPointerConstants = Internal.Runtime.FatFunctionPointerConstants;

namespace ILCompiler.DependencyAnalysis
{
    public enum GenericLookupResultReferenceType
    {
        Direct,             // The slot stores a direct pointer to the target
        Indirect,           // The slot is an indirection cell which points to the direct pointer
        ConditionalIndirect, // The slot may be a direct pointer or an indirection cell, depending on the last digit
    }

    /// <summary>
    /// Represents the result of a generic lookup within a canonical method body.
    /// The concrete artifact the generic lookup will result in can only be determined after substituting
    /// runtime determined types with a concrete generic context. Use
    /// <see cref="GetTarget(NodeFactory, Instantiation, Instantiation, GenericDictionaryNode)"/> to obtain the concrete
    /// node the result points to.
    /// </summary>
    public abstract class GenericLookupResult
    {
        protected abstract int ClassCode { get; }
        public abstract ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary);
        public abstract void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb);
        public abstract override string ToString();
        protected abstract int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer);

        public virtual void EmitDictionaryEntry(ref ObjectDataBuilder builder, NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            ISymbolNode target = GetTarget(factory, typeInstantiation, methodInstantiation, dictionary);
            if (LookupResultReferenceType(factory) == GenericLookupResultReferenceType.ConditionalIndirect)
            {
                builder.EmitPointerRelocOrIndirectionReference(target);
            }
            else
            {
                builder.EmitPointerReloc(target);
            }
        }

        public virtual GenericLookupResultReferenceType LookupResultReferenceType(NodeFactory factory)
        {
            return GenericLookupResultReferenceType.Direct;
        }

        public abstract NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory);

        // Call this api to get non-reloc dependencies that arise from use of a dictionary lookup
        public virtual IEnumerable<DependencyNodeCore<NodeFactory>> NonRelocDependenciesFromUsage(NodeFactory factory)
        {
            return Array.Empty<DependencyNodeCore<NodeFactory>>();
        }

        public class Comparer
        {
            private TypeSystemComparer _comparer;

            public Comparer(TypeSystemComparer comparer)
            {
                _comparer = comparer;
            }

            public int Compare(GenericLookupResult x, GenericLookupResult y)
            {
                if (x == y)
                {
                    return 0;
                }

                int codeX = x.ClassCode;
                int codeY = y.ClassCode;
                if (codeX == codeY)
                {
                    Debug.Assert(x.GetType() == y.GetType());

                    int result = x.CompareToImpl(y, _comparer);

                    // We did a reference equality check above so an "Equal" result is not expected
                    Debug.Assert(result != 0);

                    return result;
                }
                else
                {
                    Debug.Assert(x.GetType() != y.GetType());
                    return codeX > codeY ? -1 : 1;
                }
            }
        }
    }

    /// <summary>
    /// Generic lookup result that points to an EEType.
    /// </summary>
    internal sealed class TypeHandleGenericLookupResult : GenericLookupResult
    {
        private TypeDesc _type;

        protected override int ClassCode => 1623839081;

        public TypeHandleGenericLookupResult(TypeDesc type)
        {
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete type in a generic dictionary?");
            _type = type;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            // We are getting a constructed type symbol because this might be something passed to newobj.
            TypeDesc instantiatedType = _type.InstantiateSignature(typeInstantiation, methodInstantiation);
            return factory.ConstructedTypeSymbol(instantiatedType);
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("TypeHandle_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"TypeHandle: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.TypeHandleDictionarySlot(_type);
        }

        public override GenericLookupResultReferenceType LookupResultReferenceType(NodeFactory factory)
        {
            if (factory.CompilationModuleGroup.CanHaveReferenceThroughImportTable)
            {
                return GenericLookupResultReferenceType.ConditionalIndirect;
            }
            else
            {
                return GenericLookupResultReferenceType.Direct;
            }
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((TypeHandleGenericLookupResult)other)._type);
        }
    }


    /// <summary>
    /// Generic lookup result that points to an EEType where if the type is Nullable&lt;X&gt; the EEType is X
    /// </summary>
    internal sealed class UnwrapNullableTypeHandleGenericLookupResult : GenericLookupResult
    {
        private TypeDesc _type;

        protected override int ClassCode => 53521918;

        public UnwrapNullableTypeHandleGenericLookupResult(TypeDesc type)
        {
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete type in a generic dictionary?");
            _type = type;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            TypeDesc instantiatedType = _type.InstantiateSignature(typeInstantiation, methodInstantiation);

            // Unwrap the nullable type if necessary
            if (instantiatedType.IsNullable)
                instantiatedType = instantiatedType.Instantiation[0];

            // We are getting a constructed type symbol because this might be something passed to newobj.
            return factory.ConstructedTypeSymbol(instantiatedType);
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("UnwrapNullable_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"UnwrapNullable: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.UnwrapNullableTypeDictionarySlot(_type);
        }

        public override GenericLookupResultReferenceType LookupResultReferenceType(NodeFactory factory)
        {
            if (factory.CompilationModuleGroup.CanHaveReferenceThroughImportTable)
            {
                return GenericLookupResultReferenceType.ConditionalIndirect;
            }
            else
            {
                return GenericLookupResultReferenceType.Direct;
            }
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((UnwrapNullableTypeHandleGenericLookupResult)other)._type);
        }
    }

    /// <summary>
    /// Generic lookup result that puts a field offset into the generic dictionary.
    /// </summary>
    internal sealed class FieldOffsetGenericLookupResult : GenericLookupResult
    {
        private FieldDesc _field;

        protected override int ClassCode => -1670293557;

        public FieldOffsetGenericLookupResult(FieldDesc field)
        {
            Debug.Assert(field.OwningType.IsRuntimeDeterminedSubtype, "Concrete field in a generic dictionary?");
            _field = field;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            Debug.Assert(false, "GetTarget for a FieldOffsetGenericLookupResult doesn't make sense. It isn't a pointer being emitted");
            return null;
        }

        public override void EmitDictionaryEntry(ref ObjectDataBuilder builder, NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            FieldDesc instantiatedField = _field.InstantiateSignature(typeInstantiation, methodInstantiation);
            int offset = instantiatedField.Offset.AsInt;
            builder.EmitNaturalInt(offset);
        }


        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("FieldOffset_");
            sb.Append(nameMangler.GetMangledFieldName(_field));
        }

        public override string ToString() => $"FieldOffset: {_field}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.FieldOffsetDictionarySlot(_field);
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_field, ((FieldOffsetGenericLookupResult)other)._field);
        }
    }

    /// <summary>
    /// Generic lookup result that puts a vtable offset into the generic dictionary.
    /// </summary>
    internal sealed class VTableOffsetGenericLookupResult : GenericLookupResult
    {
        private MethodDesc _method;

        protected override int ClassCode => 386794182;

        public VTableOffsetGenericLookupResult(MethodDesc method)
        {
            Debug.Assert(method.IsRuntimeDeterminedExactMethod, "Concrete method in a generic dictionary?");
            _method = method;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            Debug.Assert(false, "GetTarget for a VTableOffsetGenericLookupResult doesn't make sense. It isn't a pointer being emitted");
            return null;
        }

        public override void EmitDictionaryEntry(ref ObjectDataBuilder builder, NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            Debug.Assert(false, "VTableOffset contents should only be generated into generic dictionaries at runtime");
            builder.EmitNaturalInt(0);
        }


        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("VTableOffset_");
            sb.Append(nameMangler.GetMangledMethodName(_method));
        }

        public override string ToString() => $"VTableOffset: {_method}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.VTableOffsetDictionarySlot(_method);
        }

        public override IEnumerable<DependencyNodeCore<NodeFactory>> NonRelocDependenciesFromUsage(NodeFactory factory)
        {
            MethodDesc canonMethod = _method.GetCanonMethodTarget(CanonicalFormKind.Universal);

            // If we're producing a full vtable for the type, we don't need to report virtual method use.
            if (factory.CompilationModuleGroup.ShouldProduceFullVTable(canonMethod.OwningType))
                return Array.Empty<DependencyNodeCore<NodeFactory>>();

            return new DependencyNodeCore<NodeFactory>[] {
                factory.VirtualMethodUse(canonMethod)
            };
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_method, ((VTableOffsetGenericLookupResult)other)._method);
        }
    }

    /// <summary>
    /// Generic lookup result that points to a RuntimeMethodHandle.
    /// </summary>
    internal sealed class MethodHandleGenericLookupResult : GenericLookupResult
    {
        private MethodDesc _method;

        protected override int ClassCode => 394272689;

        public MethodHandleGenericLookupResult(MethodDesc method)
        {
            Debug.Assert(method.IsRuntimeDeterminedExactMethod, "Concrete method in a generic dictionary?");
            _method = method;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            MethodDesc instantiatedMethod = _method.InstantiateSignature(typeInstantiation, methodInstantiation);
            return factory.RuntimeMethodHandle(instantiatedMethod);
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("MethodHandle_");
            sb.Append(nameMangler.GetMangledMethodName(_method));
        }

        public override string ToString() => $"MethodHandle: {_method}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.MethodLdTokenDictionarySlot(_method);
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_method, ((MethodHandleGenericLookupResult)other)._method);
        }
    }

    /// <summary>
    /// Generic lookup result that points to a RuntimeFieldHandle.
    /// </summary>
    internal sealed class FieldHandleGenericLookupResult : GenericLookupResult
    {
        private FieldDesc _field;

        protected override int ClassCode => -196995964;

        public FieldHandleGenericLookupResult(FieldDesc field)
        {
            Debug.Assert(field.OwningType.IsRuntimeDeterminedSubtype, "Concrete field in a generic dictionary?");
            _field = field;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            FieldDesc instantiatedField = _field.InstantiateSignature(typeInstantiation, methodInstantiation);
            return factory.RuntimeFieldHandle(instantiatedField);
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("FieldHandle_");
            sb.Append(nameMangler.GetMangledFieldName(_field));
        }

        public override string ToString() => $"FieldHandle: {_field}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.FieldLdTokenDictionarySlot(_field);
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_field, ((FieldHandleGenericLookupResult)other)._field);
        }
    }

    /// <summary>
    /// Generic lookup result that points to a method dictionary.
    /// </summary>
    internal sealed class MethodDictionaryGenericLookupResult : GenericLookupResult
    {
        private MethodDesc _method;

        protected override int ClassCode => -467418176;

        public MethodDictionaryGenericLookupResult(MethodDesc method)
        {
            Debug.Assert(method.IsRuntimeDeterminedExactMethod, "Concrete method in a generic dictionary?");
            _method = method;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            MethodDesc instantiatedMethod = _method.InstantiateSignature(typeInstantiation, methodInstantiation);
            return factory.MethodGenericDictionary(instantiatedMethod);
        }

        public override GenericLookupResultReferenceType LookupResultReferenceType(NodeFactory factory)
        {
            if (factory.CompilationModuleGroup.CanHaveReferenceThroughImportTable)
            {
                return GenericLookupResultReferenceType.ConditionalIndirect;
            }
            else
            {
                return GenericLookupResultReferenceType.Direct;
            }
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("MethodDictionary_");
            sb.Append(nameMangler.GetMangledMethodName(_method));
        }

        public override string ToString() => $"MethodDictionary: {_method}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.MethodDictionaryDictionarySlot(_method);
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_method, ((MethodDictionaryGenericLookupResult)other)._method);
        }
    }

    /// <summary>
    /// Generic lookup result that is a function pointer.
    /// </summary>
    internal sealed class MethodEntryGenericLookupResult : GenericLookupResult
    {
        private MethodDesc _method;
        private bool _isUnboxingThunk;

        protected override int ClassCode => 1572293098;

        public MethodEntryGenericLookupResult(MethodDesc method, bool isUnboxingThunk)
        {
            Debug.Assert(method.IsRuntimeDeterminedExactMethod);
            _method = method;
            _isUnboxingThunk = isUnboxingThunk;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            MethodDesc instantiatedMethod = _method.InstantiateSignature(typeInstantiation, methodInstantiation);
            return factory.FatFunctionPointer(instantiatedMethod, _isUnboxingThunk);
        }

        public override void EmitDictionaryEntry(ref ObjectDataBuilder builder, NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            builder.EmitPointerReloc(GetTarget(factory, typeInstantiation, methodInstantiation, dictionary), FatFunctionPointerConstants.Offset);
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            if (!_isUnboxingThunk)
                sb.Append("MethodEntry_");
            else
                sb.Append("UnboxMethodEntry_");

            sb.Append(nameMangler.GetMangledMethodName(_method));
        }

        public override string ToString() => $"MethodEntry: {_method}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.MethodEntrypointDictionarySlot
                        (_method, unboxing: true, functionPointerTarget: factory.MethodEntrypoint(_method.GetCanonMethodTarget(CanonicalFormKind.Specific)));
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            var otherEntry = (MethodEntryGenericLookupResult)other;
            int result = (_isUnboxingThunk ? 1 : 0) - (otherEntry._isUnboxingThunk ? 1 : 0);
            if (result != 0)
                return result;

            return comparer.Compare(_method, otherEntry._method);
        }
    }

    /// <summary>
    /// Generic lookup result that points to a virtual dispatch stub.
    /// </summary>
    internal sealed class VirtualDispatchGenericLookupResult : GenericLookupResult
    {
        private MethodDesc _method;

        protected override int ClassCode => 643566930;

        public VirtualDispatchGenericLookupResult(MethodDesc method)
        {
            Debug.Assert(method.IsRuntimeDeterminedExactMethod);
            Debug.Assert(method.IsVirtual);

            // Normal virtual methods don't need a generic lookup.
            Debug.Assert(method.OwningType.IsInterface || method.HasInstantiation);

            _method = method;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            if (factory.Target.Abi == TargetAbi.CoreRT)
            {
                MethodDesc instantiatedMethod = _method.InstantiateSignature(typeInstantiation, methodInstantiation);
                return factory.ReadyToRunHelper(ReadyToRunHelperId.VirtualCall, instantiatedMethod);
            }
            else
            {
                MethodDesc instantiatedMethod = _method.InstantiateSignature(dictionary.TypeInstantiation, dictionary.MethodInstantiation);
                return factory.InterfaceDispatchCell(instantiatedMethod, dictionary.GetMangledName(factory.NameMangler));
            }
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("VirtualCall_");
            sb.Append(nameMangler.GetMangledMethodName(_method));
        }

        public override string ToString() => $"VirtualCall: {_method}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            if (factory.Target.Abi == TargetAbi.CoreRT)
            {
                return factory.NativeLayout.NotSupportedDictionarySlot;
            }
            else
            {
                return factory.NativeLayout.InterfaceCellDictionarySlot(_method);
            }
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_method, ((VirtualDispatchGenericLookupResult)other)._method);
        }
    }

    /// <summary>
    /// Generic lookup result that points to a virtual function address load stub.
    /// </summary>
    internal sealed class VirtualResolveGenericLookupResult : GenericLookupResult
    {
        private MethodDesc _method;

        protected override int ClassCode => -12619218;

        public VirtualResolveGenericLookupResult(MethodDesc method)
        {
            Debug.Assert(method.IsRuntimeDeterminedExactMethod);
            Debug.Assert(method.IsVirtual);

            // Normal virtual methods don't need a generic lookup.
            Debug.Assert(method.OwningType.IsInterface || method.HasInstantiation);

            _method = method;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            if (factory.Target.Abi == TargetAbi.CoreRT)
            {
                MethodDesc instantiatedMethod = _method.InstantiateSignature(typeInstantiation, methodInstantiation);
                return factory.InterfaceDispatchCell(instantiatedMethod);
            }
            else
            {
                MethodDesc instantiatedMethod = _method.InstantiateSignature(dictionary.TypeInstantiation, dictionary.MethodInstantiation);
                return factory.InterfaceDispatchCell(instantiatedMethod, dictionary.GetMangledName(factory.NameMangler));
            }
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("VirtualResolve_");
            sb.Append(nameMangler.GetMangledMethodName(_method));
        }

        public override string ToString() => $"VirtualResolve: {_method}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            if (factory.Target.Abi == TargetAbi.CoreRT)
            {
                // We should be able to get rid of this custom ABI handling
                // once https://github.com/dotnet/corert/issues/3248 is fixed.
                return factory.NativeLayout.NotSupportedDictionarySlot;
            }
            else
            {
                return factory.NativeLayout.InterfaceCellDictionarySlot(_method);
            }
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_method, ((VirtualResolveGenericLookupResult)other)._method);
        }
    }

    /// <summary>
    /// Generic lookup result that points to the non-GC static base of a type.
    /// </summary>
    internal sealed class TypeNonGCStaticBaseGenericLookupResult : GenericLookupResult
    {
        private MetadataType _type;

        protected override int ClassCode => -328863267;

        public TypeNonGCStaticBaseGenericLookupResult(TypeDesc type)
        {
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete static base in a generic dictionary?");
            Debug.Assert(type is MetadataType);
            _type = (MetadataType)type;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            var instantiatedType = (MetadataType)_type.InstantiateSignature(typeInstantiation, methodInstantiation);
            return factory.Indirection(factory.TypeNonGCStaticsSymbol(instantiatedType));
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("NonGCStaticBase_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"NonGCStaticBase: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.NonGcStaticDictionarySlot(_type);
        }

        public override GenericLookupResultReferenceType LookupResultReferenceType(NodeFactory factory)
        {
            return GenericLookupResultReferenceType.Indirect;
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((TypeNonGCStaticBaseGenericLookupResult)other)._type);
        }
    }

    /// <summary>
    /// Generic lookup result that points to the threadstatic base index of a type.
    /// </summary>
    internal sealed class TypeThreadStaticBaseIndexGenericLookupResult : GenericLookupResult
    {
        private MetadataType _type;

        protected override int ClassCode => -177446371;

        public TypeThreadStaticBaseIndexGenericLookupResult(TypeDesc type)
        {
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete static base in a generic dictionary?");
            Debug.Assert(type is MetadataType);
            _type = (MetadataType)type;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            var instantiatedType = (MetadataType)_type.InstantiateSignature(typeInstantiation, methodInstantiation);
            return factory.TypeThreadStaticIndex(instantiatedType);
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("ThreadStaticBase_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"ThreadStaticBase: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.NotSupportedDictionarySlot;
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((TypeThreadStaticBaseIndexGenericLookupResult)other)._type);
        }
    }

    /// <summary>
    /// Generic lookup result that points to the GC static base of a type.
    /// </summary>
    internal sealed class TypeGCStaticBaseGenericLookupResult : GenericLookupResult
    {
        private MetadataType _type;

        protected override int ClassCode => 429225829;

        public TypeGCStaticBaseGenericLookupResult(TypeDesc type)
        {
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete static base in a generic dictionary?");
            Debug.Assert(type is MetadataType);
            _type = (MetadataType)type;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            var instantiatedType = (MetadataType)_type.InstantiateSignature(typeInstantiation, methodInstantiation);
            return factory.Indirection(factory.TypeGCStaticsSymbol(instantiatedType));
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("GCStaticBase_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"GCStaticBase: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.GcStaticDictionarySlot(_type);
        }

        public override GenericLookupResultReferenceType LookupResultReferenceType(NodeFactory factory)
        {
            return GenericLookupResultReferenceType.Indirect;
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((TypeGCStaticBaseGenericLookupResult)other)._type);
        }
    }

    /// <summary>
    /// Generic lookup result that points to an object allocator.
    /// </summary>
    internal sealed class ObjectAllocatorGenericLookupResult : GenericLookupResult
    {
        private TypeDesc _type;

        protected override int ClassCode => -1671431655;

        public ObjectAllocatorGenericLookupResult(TypeDesc type)
        {
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete type in a generic dictionary?");
            _type = type;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            TypeDesc instantiatedType = _type.InstantiateSignature(typeInstantiation, methodInstantiation);
            return factory.Indirection(factory.ExternSymbol(JitHelper.GetNewObjectHelperForType(instantiatedType)));
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("AllocObject_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"AllocObject: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.AllocateObjectDictionarySlot(_type);
        }

        public override GenericLookupResultReferenceType LookupResultReferenceType(NodeFactory factory)
        {
            return GenericLookupResultReferenceType.Indirect;
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((ObjectAllocatorGenericLookupResult)other)._type);
        }
    }

    /// <summary>
    /// Generic lookup result that points to an array allocator.
    /// </summary>
    internal sealed class ArrayAllocatorGenericLookupResult : GenericLookupResult
    {
        private TypeDesc _type;

        protected override int ClassCode => -927905284;

        public ArrayAllocatorGenericLookupResult(TypeDesc type)
        {
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete type in a generic dictionary?");
            _type = type;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            TypeDesc instantiatedType = _type.InstantiateSignature(typeInstantiation, methodInstantiation);
            Debug.Assert(instantiatedType.IsArray);
            return factory.Indirection(factory.ExternSymbol(JitHelper.GetNewArrayHelperForType(instantiatedType)));
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("AllocArray_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"AllocArray: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.AllocateArrayDictionarySlot(_type);
        }

        public override GenericLookupResultReferenceType LookupResultReferenceType(NodeFactory factory)
        {
            return GenericLookupResultReferenceType.Indirect;
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((ArrayAllocatorGenericLookupResult)other)._type);
        }
    }

    internal sealed class ThreadStaticIndexLookupResult : GenericLookupResult
    {
        private TypeDesc _type;

        protected override int ClassCode => -25938157;

        public ThreadStaticIndexLookupResult(TypeDesc type)
        {
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete type in a generic dictionary?");
            _type = type;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            UtcNodeFactory utcNodeFactory = factory as UtcNodeFactory;
            Debug.Assert(utcNodeFactory != null);
            TypeDesc instantiatedType = _type.InstantiateSignature(typeInstantiation, methodInstantiation);
            return factory.Indirection(utcNodeFactory.TypeThreadStaticsIndexSymbol(instantiatedType));
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("TlsIndex_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"ThreadStaticIndex: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.TlsIndexDictionarySlot(_type);
        }

        public override GenericLookupResultReferenceType LookupResultReferenceType(NodeFactory factory)
        {
            return GenericLookupResultReferenceType.Indirect;
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((ThreadStaticIndexLookupResult)other)._type);
        }
    }

    internal sealed class ThreadStaticOffsetLookupResult : GenericLookupResult
    {
        private TypeDesc _type;

        protected override int ClassCode => -1678275787;

        public ThreadStaticOffsetLookupResult(TypeDesc type)
        {
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete type in a generic dictionary?");
            _type = type;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            UtcNodeFactory utcNodeFactory = factory as UtcNodeFactory;
            Debug.Assert(utcNodeFactory != null);
            TypeDesc instantiatedType = _type.InstantiateSignature(typeInstantiation, methodInstantiation);
            Debug.Assert(instantiatedType is MetadataType);
            return factory.Indirection(utcNodeFactory.TypeThreadStaticsOffsetSymbol((MetadataType)instantiatedType));
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("TlsOffset_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"ThreadStaticOffset: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.TlsOffsetDictionarySlot(_type);
        }

        public override GenericLookupResultReferenceType LookupResultReferenceType(NodeFactory factory)
        {
            return GenericLookupResultReferenceType.Indirect;
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((ThreadStaticOffsetLookupResult)other)._type);
        }
    }

    internal sealed class DefaultConstructorLookupResult : GenericLookupResult
    {
        private TypeDesc _type;

        protected override int ClassCode => -1391112482;

        public DefaultConstructorLookupResult(TypeDesc type)
        {
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete type in a generic dictionary?");
            _type = type;
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            TypeDesc instantiatedType = _type.InstantiateSignature(typeInstantiation, methodInstantiation);
            MethodDesc defaultCtor = instantiatedType.GetDefaultConstructor();

            if (defaultCtor == null)
            {
                // If there isn't a default constructor, use the fallback one.
                MetadataType missingCtorType = factory.TypeSystemContext.SystemModule.GetKnownType("System", "Activator");
                missingCtorType = missingCtorType.GetNestedType("ClassWithMissingConstructor");                
                Debug.Assert(missingCtorType != null);
                defaultCtor = missingCtorType.GetParameterlessConstructor();
            }
            else
            {
                defaultCtor = defaultCtor.GetCanonMethodTarget(CanonicalFormKind.Specific);
            }

            return factory.MethodEntrypoint(defaultCtor);
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("DefaultCtor_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"DefaultConstructor: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.DefaultConstructorDictionarySlot(_type);
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((DefaultConstructorLookupResult)other)._type);
        }
    }

    internal sealed class CallingConventionConverterLookupResult : GenericLookupResult
    {
        private CallingConventionConverterKey _callingConventionConverter;

        protected override int ClassCode => -581806472;

        public CallingConventionConverterLookupResult(CallingConventionConverterKey callingConventionConverter)
        {
            _callingConventionConverter = callingConventionConverter;
            Debug.Assert(Internal.Runtime.UniversalGenericParameterLayout.MethodSignatureHasVarsNeedingCallingConventionConverter(callingConventionConverter.Signature));
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            Debug.Assert(false, "GetTarget for a CallingConventionConverterLookupResult doesn't make sense. It isn't a pointer being emitted");
            return null;
        }

        public override void EmitDictionaryEntry(ref ObjectDataBuilder builder, NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            Debug.Assert(false, "CallingConventionConverterLookupResult contents should only be generated into generic dictionaries at runtime");
            builder.EmitNaturalInt(0);
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("CallingConventionConverterLookupResult_");
            sb.Append(_callingConventionConverter.GetName());
        }

        public override string ToString() => "CallingConventionConverterLookupResult";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.CallingConventionConverter(_callingConventionConverter);
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            var otherEntry = (CallingConventionConverterLookupResult)other;
            int result = (int)(_callingConventionConverter.ConverterKind - otherEntry._callingConventionConverter.ConverterKind);
            if (result != 0)
                return result;

            return comparer.Compare(_callingConventionConverter.Signature, otherEntry._callingConventionConverter.Signature);
        }
    }

    internal sealed class TypeSizeLookupResult : GenericLookupResult
    {
        private TypeDesc _type;

        protected override int ClassCode => -367755250;

        public TypeSizeLookupResult(TypeDesc type)
        {
            _type = type;
            Debug.Assert(type.IsRuntimeDeterminedSubtype, "Concrete type in a generic dictionary?");
        }
        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            Debug.Assert(false, "GetTarget for a TypeSizeLookupResult doesn't make sense. It isn't a pointer being emitted");
            return null;
        }

        public override void EmitDictionaryEntry(ref ObjectDataBuilder builder, NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            TypeDesc instantiatedType = _type.InstantiateSignature(typeInstantiation, methodInstantiation);
            int typeSize;

            if (_type.IsDefType)
            {
                typeSize = ((DefType)_type).InstanceFieldSize.AsInt;
            }
            else
            {
                typeSize = factory.TypeSystemContext.Target.PointerSize;
            }

            builder.EmitNaturalInt(typeSize);
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("TypeSize_");
            sb.Append(nameMangler.GetMangledTypeName(_type));
        }

        public override string ToString() => $"TypeSize: {_type}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.TypeSizeDictionarySlot(_type);
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            return comparer.Compare(_type, ((TypeSizeLookupResult)other)._type);
        }
    }

    internal sealed class ConstrainedMethodUseLookupResult : GenericLookupResult
    {
        MethodDesc _constrainedMethod;
        TypeDesc _constraintType;
        bool _directCall;

        protected override int ClassCode => -1525377658;

        public ConstrainedMethodUseLookupResult(MethodDesc constrainedMethod, TypeDesc constraintType, bool directCall)
        {
            _constrainedMethod = constrainedMethod;
            _constraintType = constraintType;
            _directCall = directCall;

            Debug.Assert(_constraintType.IsRuntimeDeterminedSubtype || _constrainedMethod.IsRuntimeDeterminedExactMethod, "Concrete type in a generic dictionary?");
            Debug.Assert(!_constrainedMethod.HasInstantiation || !_directCall, "Direct call to constrained generic method isn't supported");
        }

        public override ISymbolNode GetTarget(NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            MethodDesc instantiatedConstrainedMethod = _constrainedMethod.InstantiateSignature(typeInstantiation, methodInstantiation);
            TypeDesc instantiatedConstraintType = _constraintType.InstantiateSignature(typeInstantiation, methodInstantiation);

            MethodDesc implMethod = instantiatedConstraintType.GetClosestDefType().ResolveInterfaceMethodToVirtualMethodOnType(instantiatedConstrainedMethod);

            // AOT use of this generic lookup is restricted to finding methods on valuetypes (runtime usage of this slot in universal generics is more flexible)
            Debug.Assert(instantiatedConstraintType.IsValueType);
            Debug.Assert(implMethod.OwningType == instantiatedConstraintType);

            if (implMethod.HasInstantiation && implMethod.GetCanonMethodTarget(CanonicalFormKind.Specific) != implMethod)
            {
                return factory.FatFunctionPointer(implMethod);
            }
            else
            {
                return factory.MethodEntrypoint(implMethod);
            }
        }

        public override void EmitDictionaryEntry(ref ObjectDataBuilder builder, NodeFactory factory, Instantiation typeInstantiation, Instantiation methodInstantiation, GenericDictionaryNode dictionary)
        {
            ISymbolNode target = GetTarget(factory, typeInstantiation, methodInstantiation, dictionary);
            if (target is IFatFunctionPointerNode)
            {
                builder.EmitPointerReloc(target, FatFunctionPointerConstants.Offset);
            }
            else
            {
                builder.EmitPointerReloc(target);
            }
        }

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("ConstrainedMethodUseLookupResult_");
            sb.Append(nameMangler.GetMangledTypeName(_constraintType));
            sb.Append(nameMangler.GetMangledMethodName(_constrainedMethod));
            if (_directCall)
                sb.Append("Direct");
        }

        public override string ToString() => $"ConstrainedMethodUseLookupResult: {_constraintType} {_constrainedMethod} {_directCall}";

        public override NativeLayoutVertexNode TemplateDictionaryNode(NodeFactory factory)
        {
            return factory.NativeLayout.ConstrainedMethodUse(_constrainedMethod, _constraintType, _directCall);
        }

        protected override int CompareToImpl(GenericLookupResult other, TypeSystemComparer comparer)
        {
            var otherResult = (ConstrainedMethodUseLookupResult)other;
            int result = (_directCall ? 1 : 0) - (otherResult._directCall ? 1 : 0);
            if (result != 0)
                return result;

            result = comparer.Compare(_constraintType, otherResult._constraintType);
            if (result != 0)
                return result;

            return comparer.Compare(_constrainedMethod, otherResult._constrainedMethod);
        }
    }
}
