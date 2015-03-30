﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using NUnit.Framework;

using Microsoft.Build.Tasks;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.UnitTests
{
    [TestFixture]
    sealed public class WinMDExpTests
    {
        /// <summary>
        /// Tests the "References" parameter on the winmdexp task, and confirms that it sets
        /// the /reference switch on the command-line correctly.  
        /// </summary>
        [Test]
        public void References()
        {
            WinMDExp t = new WinMDExp();
            t.WinMDModule = "Foo.dll";

            TaskItem mscorlibReference = new TaskItem("mscorlib.dll");
            TaskItem windowsFoundationReference = new TaskItem("Windows.Foundation.winmd");

            t.References = new TaskItem[] { mscorlibReference, windowsFoundationReference };
            CommandLine.ValidateHasParameter(
                t,
                CommandLineBuilder.FixCommandLineSwitch("/reference:mscorlib.dll"),
                false);
            CommandLine.ValidateHasParameter(
                t,
                CommandLineBuilder.FixCommandLineSwitch("/reference:Windows.Foundation.winmd"),
                false);
        }

        [Test]
        public void TestNoWarnSwitchWithWarnings()
        {
            WinMDExp t = new WinMDExp();
            t.WinMDModule = "Foo.dll";
            t.DisabledWarnings = "41999,42016";
            CommandLine.ValidateHasParameter(t, CommandLineBuilder.FixCommandLineSwitch("/nowarn:41999,42016"), false);
        }


        // Tests the "GenerateDocumentation" and "DocumentationFile" parameters on the Vbc task,
        // and confirms that it sets the /doc switch on the command-line correctly.
        [Test]
        public void DocumentationFile()
        {
            WinMDExp t = new WinMDExp();

            t.WinMDModule = "Foo.dll";
            t.OutputDocumentationFile = "output.xml";
            t.InputDocumentationFile = "input.xml";

            CommandLine.ValidateHasParameter(t, CommandLineBuilder.FixCommandLineSwitch("/d:output.xml"), false);
            CommandLine.ValidateHasParameter(t, CommandLineBuilder.FixCommandLineSwitch("/md:input.xml"), false);
        }

        [Test]
        public void PDBFileTesting()
        {
            WinMDExp t = new WinMDExp();
            t.WinMDModule = "Foo.dll";
            t.OutputWindowsMetadataFile = "Foo.dll";
            t.OutputPDBFile = "output.pdb";
            t.InputPDBFile = "input.pdb";

            CommandLine.ValidateHasParameter(t, CommandLineBuilder.FixCommandLineSwitch("/pdb:output.pdb"), false);
            CommandLine.ValidateHasParameter(t, CommandLineBuilder.FixCommandLineSwitch("/mp:input.pdb"), false);
        }

        [Test]
        public void WinMDModule()
        {
            WinMDExp t = new WinMDExp();

            t.WinMDModule = "Foo.dll";
            CommandLine.ValidateContains(t, "Foo.dll", false);
        }

        [Test]
        public void UsesrDefinedOutputFile()
        {
            WinMDExp t = new WinMDExp();
            t.WinMDModule = "Foo.dll";
            t.OutputWindowsMetadataFile = "Bob.winmd";
            CommandLine.ValidateHasParameter(t, CommandLineBuilder.FixCommandLineSwitch("/out:Bob.winmd"), false);
        }

        [Test]
        public void NoOutputFileDefined()
        {
            WinMDExp t = new WinMDExp();

            t.WinMDModule = "Foo.dll";
            t.OutputWindowsMetadataFile = "Foo.winmd";
            CommandLine.ValidateHasParameter(t, CommandLineBuilder.FixCommandLineSwitch("/out:Foo.winmd"), false);
        }
    }
}





