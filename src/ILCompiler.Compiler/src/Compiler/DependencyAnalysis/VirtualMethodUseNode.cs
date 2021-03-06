// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

using ILCompiler.DependencyAnalysisFramework;

using Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler.DependencyAnalysis
{
    // This node represents the concept of a virtual method being used.
    // It has no direct depedencies, but may be referred to by conditional static 
    // dependencies, or static dependencies from elsewhere.
    //
    // It is used to keep track of uses of virtual methods to ensure that the
    // vtables are properly constructed
    internal class VirtualMethodUseNode : DependencyNodeCore<NodeFactory>
    {
        private MethodDesc _decl;

        public MethodDesc Method => _decl;

        public VirtualMethodUseNode(MethodDesc decl)
        {
            Debug.Assert(decl.IsVirtual);

            // Virtual method use always represents the slot defining method of the virtual.
            // Places that might see virtual methods being used through an override need to normalize
            // to the slot defining method.
            Debug.Assert(MetadataVirtualMethodAlgorithm.FindSlotDefiningMethodForVirtualMethod(decl) == decl);

            // Generic virtual methods are tracked by an orthogonal mechanism.
            Debug.Assert(!decl.HasInstantiation);

            _decl = decl;
        }

        protected override string GetName(NodeFactory factory) => $"VirtualMethodUse {_decl.ToString()}";

        protected override void OnMarked(NodeFactory factory)
        {
            // If the VTable slice is getting built on demand, the fact that the virtual method is used means
            // that the slot is used.
            var lazyVTableSlice = factory.VTable(_decl.OwningType) as LazilyBuiltVTableSliceNode;
            if (lazyVTableSlice != null)
                lazyVTableSlice.AddEntry(factory, _decl);
        }

        public override bool HasConditionalStaticDependencies => _decl.Context.SupportsUniversalCanon && _decl.OwningType.HasInstantiation && !_decl.OwningType.IsInterface;
        public override bool HasDynamicDependencies => false;
        public override bool InterestingForDynamicDependencyAnalysis => false;

        public override bool StaticDependenciesAreComputed => true;

        public override IEnumerable<DependencyListEntry> GetStaticDependencies(NodeFactory factory)
        {
            DependencyList dependencies = new DependencyList();

            // TODO: https://github.com/dotnet/corert/issues/3224
            // Reflection invoke stub handling is here because in the current reflection model we reflection-enable
            // all methods that are compiled. Ideally the list of reflection enabled methods should be known before
            // we even start the compilation process (with the invocation stubs being compilation roots like any other).
            // The existing model has it's problems: e.g. the invocability of the method depends on inliner decisions.
            if (factory.MetadataManager.IsReflectionInvokable(_decl) && _decl.IsAbstract)
            {
                if (factory.MetadataManager.HasReflectionInvokeStubForInvokableMethod(_decl) && !_decl.IsCanonicalMethod(CanonicalFormKind.Any))
                {
                    MethodDesc invokeStub = factory.MetadataManager.GetReflectionInvokeStub(_decl);
                    MethodDesc canonInvokeStub = invokeStub.GetCanonMethodTarget(CanonicalFormKind.Specific);
                    if (invokeStub != canonInvokeStub)
                    {
                        dependencies.Add(new DependencyListEntry(factory.MetadataManager.DynamicInvokeTemplateData, "Reflection invoke template data"));
                        factory.MetadataManager.DynamicInvokeTemplateData.AddDependenciesDueToInvokeTemplatePresence(ref dependencies, factory, canonInvokeStub);
                    }
                    else
                        dependencies.Add(new DependencyListEntry(factory.MethodEntrypoint(invokeStub), "Reflection invoke"));
                }

                dependencies.AddRange(ReflectionVirtualInvokeMapNode.GetVirtualInvokeMapDependencies(factory, _decl));
            }

            MethodDesc canonDecl = _decl.GetCanonMethodTarget(CanonicalFormKind.Specific);
            if (canonDecl != _decl)
                dependencies.Add(new DependencyListEntry(factory.VirtualMethodUse(canonDecl), "Canonical method"));

            dependencies.Add(new DependencyListEntry(factory.VTable(_decl.OwningType), "VTable of a VirtualMethodUse"));

            return dependencies;
        }

        public override IEnumerable<CombinedDependencyListEntry> GetConditionalStaticDependencies(NodeFactory factory)
        {
            Debug.Assert(_decl.OwningType.HasInstantiation);
            Debug.Assert(!_decl.OwningType.IsInterface);
            Debug.Assert(factory.TypeSystemContext.SupportsUniversalCanon);

            DefType universalCanonicalOwningType = (DefType)_decl.OwningType.ConvertToCanonForm(CanonicalFormKind.Universal);
            Debug.Assert(universalCanonicalOwningType.IsCanonicalSubtype(CanonicalFormKind.Universal));

            if (!factory.CompilationModuleGroup.ShouldProduceFullVTable(universalCanonicalOwningType))
            {
                // This code ensures that in cases where we don't structurally force all universal canonical instantiations
                // to have full vtables, that we ensure that all vtables are equivalently shaped between universal and non-universal types
                return new CombinedDependencyListEntry[] {
                    new CombinedDependencyListEntry(
                        factory.VirtualMethodUse(_decl.GetCanonMethodTarget(CanonicalFormKind.Universal)),
                        factory.NativeLayout.TemplateTypeLayout(universalCanonicalOwningType),
                        "If universal canon instantiation of method exists, ensure that the universal canonical type has the right set of dependencies")
                };
            }
            else
            {
                return Array.Empty<CombinedDependencyListEntry>();
            }
        }

        public override IEnumerable<CombinedDependencyListEntry> SearchDynamicDependencies(List<DependencyNodeCore<NodeFactory>> markedNodes, int firstNode, NodeFactory factory) => null;
    }
}
