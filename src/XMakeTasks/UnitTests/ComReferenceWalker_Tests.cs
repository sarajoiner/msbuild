﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Microsoft.Build.Tasks;
using System.Runtime.InteropServices.ComTypes;

using Marshal = System.Runtime.InteropServices.Marshal;
using COMException = System.Runtime.InteropServices.COMException;

namespace Microsoft.Build.UnitTests
{
    [TestFixture]
    public class ComReferenceWalker_Tests
    {
        static private int MockReleaseComObject(object o)
        {
            return 0;
        }

        private void AssertDependenciesContainTypeLib(TYPELIBATTR[] dependencies, MockTypeLib typeLib, bool contains)
        {
            AssertDependenciesContainTypeLib("", dependencies, typeLib, contains);
        }

        private void AssertDependenciesContainTypeLib(string message, TYPELIBATTR[] dependencies, MockTypeLib typeLib, bool contains)
        {
            bool dependencyExists = false;

            foreach (TYPELIBATTR attr in dependencies)
            {
                if (attr.guid == typeLib.Attributes.guid)
                {
                    dependencyExists = true;
                    break;
                }
            }

            Assert.AreEqual(contains, dependencyExists, message);
        }

        [Test]
        public void WalkTypeInfosInEmptyLibrary()
        {
            MockTypeLib typeLib = new MockTypeLib();

            ComDependencyWalker walker = new ComDependencyWalker(new MarshalReleaseComObject(MockReleaseComObject));
            walker.AnalyzeTypeLibrary(typeLib);
            Assert.AreEqual(0, walker.GetDependencies().GetLength(0));

            typeLib.AssertAllHandlesReleased();
        }

        private void CreateTwoTypeLibs(out MockTypeLib mainTypeLib, out MockTypeLib dependencyTypeLib)
        {
            mainTypeLib = new MockTypeLib();
            mainTypeLib.AddTypeInfo(new MockTypeInfo());

            dependencyTypeLib = new MockTypeLib();
            dependencyTypeLib.AddTypeInfo(new MockTypeInfo());
        }

        private TYPELIBATTR[] RunDependencyWalker(MockTypeLib mainTypeLib, MockTypeLib dependencyTypeLib, bool dependencyShouldBePresent)
        {
            ComDependencyWalker walker = new ComDependencyWalker(new MarshalReleaseComObject(MockReleaseComObject));
            walker.AnalyzeTypeLibrary(mainTypeLib);

            TYPELIBATTR[] dependencies = walker.GetDependencies();

            // types from the main type library should be in the dependency list
            AssertDependenciesContainTypeLib(dependencies, mainTypeLib, true);

            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib, dependencyShouldBePresent);

            mainTypeLib.AssertAllHandlesReleased();
            dependencyTypeLib.AssertAllHandlesReleased();

            return dependencies;
        }

        /// <summary>
        /// A type in the main type library implements an interface from a dependent type library
        /// </summary>
        [Test]
        public void ImplementedInterfaces()
        {
            MockTypeLib mainTypeLib, dependencyTypeLib;
            CreateTwoTypeLibs(out mainTypeLib, out dependencyTypeLib);

            mainTypeLib.ContainedTypeInfos[0].ImplementsInterface(dependencyTypeLib.ContainedTypeInfos[0]);

            RunDependencyWalker(mainTypeLib, dependencyTypeLib, true);
        }

        [Test]
        public void DefinedVariableUDT()
        {
            MockTypeLib mainTypeLib, dependencyTypeLib;
            CreateTwoTypeLibs(out mainTypeLib, out dependencyTypeLib);

            mainTypeLib.ContainedTypeInfos[0].DefinesVariable(dependencyTypeLib.ContainedTypeInfos[0]);

            RunDependencyWalker(mainTypeLib, dependencyTypeLib, true);
        }

        [Test]
        public void DefinedVariableUDTArray()
        {
            MockTypeLib mainTypeLib, dependencyTypeLib;
            CreateTwoTypeLibs(out mainTypeLib, out dependencyTypeLib);

            mainTypeLib.ContainedTypeInfos[0].DefinesVariable(new ArrayCompositeTypeInfo(dependencyTypeLib.ContainedTypeInfos[0]));

            RunDependencyWalker(mainTypeLib, dependencyTypeLib, true);
        }

        [Test]
        public void DefinedVariableUDTPtr()
        {
            MockTypeLib mainTypeLib, dependencyTypeLib;
            CreateTwoTypeLibs(out mainTypeLib, out dependencyTypeLib);

            mainTypeLib.ContainedTypeInfos[0].DefinesVariable(new PtrCompositeTypeInfo(dependencyTypeLib.ContainedTypeInfos[0]));

            RunDependencyWalker(mainTypeLib, dependencyTypeLib, true);
        }

        [Test]
        public void ThereAndBackAgain()
        {
            MockTypeLib mainTypeLib, dependencyTypeLib;
            CreateTwoTypeLibs(out mainTypeLib, out dependencyTypeLib);

            mainTypeLib.ContainedTypeInfos[0].DefinesVariable(new PtrCompositeTypeInfo(dependencyTypeLib.ContainedTypeInfos[0]));
            dependencyTypeLib.ContainedTypeInfos[0].ImplementsInterface(mainTypeLib.ContainedTypeInfos[0]);

            RunDependencyWalker(mainTypeLib, dependencyTypeLib, true);
        }

        [Test]
        public void ComplexComposition()
        {
            MockTypeLib mainTypeLib, dependencyTypeLib;
            CreateTwoTypeLibs(out mainTypeLib, out dependencyTypeLib);

            mainTypeLib.ContainedTypeInfos[0].DefinesVariable(
                new ArrayCompositeTypeInfo(new ArrayCompositeTypeInfo(new PtrCompositeTypeInfo(
                    new PtrCompositeTypeInfo(new ArrayCompositeTypeInfo(new PtrCompositeTypeInfo(dependencyTypeLib.ContainedTypeInfos[0])))))));

            RunDependencyWalker(mainTypeLib, dependencyTypeLib, true);
        }

        [Test]
        public void DefinedFunction()
        {
            MockTypeLib mainTypeLib, dependencyTypeLib1, dependencyTypeLib2, dependencyTypeLib3;
            CreateTwoTypeLibs(out mainTypeLib, out dependencyTypeLib1);
            CreateTwoTypeLibs(out dependencyTypeLib2, out dependencyTypeLib3);

            mainTypeLib.ContainedTypeInfos[0].DefinesFunction(
                new MockTypeInfo[] { dependencyTypeLib1.ContainedTypeInfos[0], dependencyTypeLib2.ContainedTypeInfos[0] },
                dependencyTypeLib3.ContainedTypeInfos[0]);

            TYPELIBATTR[] dependencies = RunDependencyWalker(mainTypeLib, dependencyTypeLib1, true);

            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib2, true);
            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib3, true);

            dependencyTypeLib2.AssertAllHandlesReleased();
            dependencyTypeLib3.AssertAllHandlesReleased();
        }

        [Test]
        public void IgnoreKnownOleTypes()
        {
            MockTypeLib mainTypeLib = new MockTypeLib();
            mainTypeLib.AddTypeInfo(new MockTypeInfo());

            MockTypeLib oleTypeLib = new MockTypeLib();
            oleTypeLib.AddTypeInfo(new MockTypeInfo(NativeMethods.IID_IUnknown));
            oleTypeLib.AddTypeInfo(new MockTypeInfo(NativeMethods.IID_IDispatch));
            oleTypeLib.AddTypeInfo(new MockTypeInfo(NativeMethods.IID_IDispatchEx));
            oleTypeLib.AddTypeInfo(new MockTypeInfo(NativeMethods.IID_IEnumVariant));
            oleTypeLib.AddTypeInfo(new MockTypeInfo(NativeMethods.IID_ITypeInfo));

            // We don't check for this type in the ComDependencyWalker, so it doesn't get counted as a known OLE type.
            // It's too late in the Dev10 cycle to add it to shipping code without phenomenally good reason, but we should
            // re-examine this in Dev11.  
            // oleTypeLib.AddTypeInfo(new MockTypeInfo(TYPEKIND.TKIND_ENUM));

            foreach (MockTypeInfo typeInfo in oleTypeLib.ContainedTypeInfos)
            {
                mainTypeLib.ContainedTypeInfos[0].DefinesVariable(typeInfo);
            }

            RunDependencyWalker(mainTypeLib, oleTypeLib, false);
        }

        [Test]
        public void IgnoreGuidType()
        {
            MockTypeLib mainTypeLib = new MockTypeLib();
            mainTypeLib.AddTypeInfo(new MockTypeInfo());

            MockTypeLib oleTypeLib = new MockTypeLib(NativeMethods.IID_StdOle);
            oleTypeLib.AddTypeInfo(new MockTypeInfo());
            oleTypeLib.ContainedTypeInfos[0].TypeName = "GUID";

            mainTypeLib.ContainedTypeInfos[0].DefinesVariable(oleTypeLib.ContainedTypeInfos[0]);

            RunDependencyWalker(mainTypeLib, oleTypeLib, false);
        }

        [Test]
        public void IgnoreNetExportedTypeLibs()
        {
            MockTypeLib mainTypeLib, dependencyTypeLib;
            CreateTwoTypeLibs(out mainTypeLib, out dependencyTypeLib);

            mainTypeLib.ContainedTypeInfos[0].DefinesFunction(
                new MockTypeInfo[] { dependencyTypeLib.ContainedTypeInfos[0] }, dependencyTypeLib.ContainedTypeInfos[0]);
            dependencyTypeLib.ExportedFromComPlus = "1";

            RunDependencyWalker(mainTypeLib, dependencyTypeLib, false);
        }

        /// <summary>
        /// The main type lib is broken... don't expect any results, but make sure we don't throw.
        /// </summary>
        [Test]
        public void FaultInjectionMainLib()
        {
            // The primary test here is that we don't throw, which can't be explicitly expressed in NUnit...
            // other asserts are secondary
            foreach (MockTypeLibrariesFailurePoints failurePoint in Enum.GetValues(typeof(MockTypeLibrariesFailurePoints)))
            {
                MockTypeLib mainTypeLib = new MockTypeLib();
                mainTypeLib.AddTypeInfo(new MockTypeInfo());

                // Make it the StdOle lib to exercise the ITypeInfo.GetDocumentation failure point
                MockTypeLib dependencyTypeLib = new MockTypeLib(NativeMethods.IID_StdOle);
                dependencyTypeLib.AddTypeInfo(new MockTypeInfo());

                COMException failureException = new COMException("unhandled exception in " + failurePoint.ToString());
                mainTypeLib.InjectFailure(failurePoint, failureException);
                dependencyTypeLib.InjectFailure(failurePoint, failureException);

                mainTypeLib.ContainedTypeInfos[0].ImplementsInterface(dependencyTypeLib.ContainedTypeInfos[0]);
                mainTypeLib.ContainedTypeInfos[0].DefinesVariable(dependencyTypeLib.ContainedTypeInfos[0]);
                mainTypeLib.ContainedTypeInfos[0].DefinesFunction(
                    new MockTypeInfo[] { dependencyTypeLib.ContainedTypeInfos[0] }, dependencyTypeLib.ContainedTypeInfos[0]);

                ComDependencyWalker walker = new ComDependencyWalker(new MarshalReleaseComObject(MockReleaseComObject));
                walker.AnalyzeTypeLibrary(mainTypeLib);

                Assert.AreEqual(1, walker.EncounteredProblems.Count, "Test failed for failure point " + failurePoint.ToString());
                Assert.AreEqual(failureException, walker.EncounteredProblems[0], "Test failed for failure point " + failurePoint.ToString());

                mainTypeLib.AssertAllHandlesReleased();
                dependencyTypeLib.AssertAllHandlesReleased();
            }
        }

        private static void CreateFaultInjectionTypeLibs(MockTypeLibrariesFailurePoints failurePoint, out MockTypeLib mainTypeLib,
            out MockTypeLib dependencyTypeLibGood1, out MockTypeLib dependencyTypeLibBad1,
            out MockTypeLib dependencyTypeLibGood2, out MockTypeLib dependencyTypeLibBad2)
        {
            mainTypeLib = new MockTypeLib();
            mainTypeLib.AddTypeInfo(new MockTypeInfo());
            mainTypeLib.AddTypeInfo(new MockTypeInfo());

            dependencyTypeLibGood1 = new MockTypeLib();
            dependencyTypeLibGood1.AddTypeInfo(new MockTypeInfo());

            // Make it the StdOle lib to exercise the ITypeInfo.GetDocumentation failure point
            dependencyTypeLibBad1 = new MockTypeLib(NativeMethods.IID_StdOle);
            dependencyTypeLibBad1.AddTypeInfo(new MockTypeInfo());

            dependencyTypeLibGood2 = new MockTypeLib();
            dependencyTypeLibGood2.AddTypeInfo(new MockTypeInfo());

            // Make it the StdOle lib to exercise the ITypeInfo.GetDocumentation failure point
            dependencyTypeLibBad2 = new MockTypeLib(NativeMethods.IID_StdOle);
            dependencyTypeLibBad2.AddTypeInfo(new MockTypeInfo());

            COMException failureException = new COMException("unhandled exception in " + failurePoint.ToString());

            dependencyTypeLibBad1.InjectFailure(failurePoint, failureException);
            dependencyTypeLibBad2.InjectFailure(failurePoint, failureException);
        }

        private void RunDependencyWalkerFaultInjection(MockTypeLibrariesFailurePoints failurePoint, MockTypeLib mainTypeLib, MockTypeLib dependencyTypeLibGood1, MockTypeLib dependencyTypeLibBad1, MockTypeLib dependencyTypeLibGood2, MockTypeLib dependencyTypeLibBad2)
        {
            ComDependencyWalker walker = new ComDependencyWalker(new MarshalReleaseComObject(MockReleaseComObject));
            walker.AnalyzeTypeLibrary(mainTypeLib);

            // Did the current failure point get hit for this test? If not then no point in checking anything
            // The previous test (FaultInjectionMainLib) ensures that all defined failure points actually 
            // cause some sort of trouble
            if (walker.EncounteredProblems.Count > 0)
            {
                TYPELIBATTR[] dependencies = walker.GetDependencies();
                AssertDependenciesContainTypeLib("Test failed for failure point " + failurePoint.ToString(),
                    dependencies, mainTypeLib, true);
                AssertDependenciesContainTypeLib("Test failed for failure point " + failurePoint.ToString(),
                    dependencies, dependencyTypeLibGood1, true);
                AssertDependenciesContainTypeLib("Test failed for failure point " + failurePoint.ToString(),
                    dependencies, dependencyTypeLibGood2, true);
                AssertDependenciesContainTypeLib("Test failed for failure point " + failurePoint.ToString(),
                    dependencies, dependencyTypeLibBad1, false);
                AssertDependenciesContainTypeLib("Test failed for failure point " + failurePoint.ToString(),
                    dependencies, dependencyTypeLibBad2, false);
            }

            mainTypeLib.AssertAllHandlesReleased();
            dependencyTypeLibGood1.AssertAllHandlesReleased();
            dependencyTypeLibGood2.AssertAllHandlesReleased();
            dependencyTypeLibBad1.AssertAllHandlesReleased();
            dependencyTypeLibBad2.AssertAllHandlesReleased();
        }

        [Test]
        public void FullDependenciesWithIncrementalAnalysis()
        {
            MockTypeLib mainTypeLib1, mainTypeLib2, mainTypeLib3, dependencyTypeLib1, dependencyTypeLib2, dependencyTypeLib3;
            CreateTwoTypeLibs(out mainTypeLib1, out dependencyTypeLib1);
            CreateTwoTypeLibs(out mainTypeLib2, out dependencyTypeLib2);
            CreateTwoTypeLibs(out mainTypeLib3, out dependencyTypeLib3);

            mainTypeLib1.ContainedTypeInfos[0].DefinesVariable(dependencyTypeLib1.ContainedTypeInfos[0]);

            mainTypeLib2.ContainedTypeInfos[0].DefinesVariable(dependencyTypeLib1.ContainedTypeInfos[0]);
            mainTypeLib2.ContainedTypeInfos[0].DefinesVariable(dependencyTypeLib2.ContainedTypeInfos[0]);

            mainTypeLib3.ContainedTypeInfos[0].DefinesVariable(dependencyTypeLib1.ContainedTypeInfos[0]);
            mainTypeLib3.ContainedTypeInfos[0].DefinesVariable(dependencyTypeLib3.ContainedTypeInfos[0]);

            ComDependencyWalker walker = new ComDependencyWalker(MockReleaseComObject);

            walker.AnalyzeTypeLibrary(mainTypeLib1);
            TYPELIBATTR[] dependencies = walker.GetDependencies();
            ICollection<string> analyzedTypes = walker.GetAnalyzedTypeNames();

            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib1, true);
            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib2, false);
            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib3, false);
            Assert.AreEqual(2, analyzedTypes.Count);

            walker.ClearDependencyList();
            walker.AnalyzeTypeLibrary(mainTypeLib2);
            dependencies = walker.GetDependencies();
            analyzedTypes = walker.GetAnalyzedTypeNames();

            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib1, true);
            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib2, true);
            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib3, false);
            Assert.AreEqual(4, analyzedTypes.Count);

            walker.ClearDependencyList();
            walker.AnalyzeTypeLibrary(mainTypeLib3);
            dependencies = walker.GetDependencies();
            analyzedTypes = walker.GetAnalyzedTypeNames();

            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib1, true);
            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib2, false);
            AssertDependenciesContainTypeLib(dependencies, dependencyTypeLib3, true);
            Assert.AreEqual(6, analyzedTypes.Count);
        }
    }
}
