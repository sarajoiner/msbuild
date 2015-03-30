﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//-----------------------------------------------------------------------
// <copyright file="Project_Tests.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// <summary>Tests for Project</summary>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;
using Microsoft.Build.Utilities;

using NUnit.Framework;

using Task = System.Threading.Tasks.Task;
using InvalidProjectFileException = Microsoft.Build.Exceptions.InvalidProjectFileException;
using ToolLocationHelper = Microsoft.Build.Utilities.ToolLocationHelper;
using TargetDotNetFrameworkVersion = Microsoft.Build.Utilities.TargetDotNetFrameworkVersion;

namespace Microsoft.Build.UnitTests.OM.Definition
{
    /// <summary>
    /// Tests for Project public members
    /// </summary>
    [TestFixture]
    public class Project_Tests
    {
        /// <summary>
        /// Clear out the global project collection
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            ProjectCollection.GlobalProjectCollection.UnloadAllProjects();
        }

        /// <summary>
        /// Clear out the global project collection
        /// </summary>
        [TearDown]
        public void TearDown()
        {
            ProjectCollection.GlobalProjectCollection.UnloadAllProjects();
            Assert.AreEqual(0, ProjectCollection.GlobalProjectCollection.Count);

            IDictionary<string, string> globalProperties = ProjectCollection.GlobalProjectCollection.GlobalProperties;
            foreach (string propertyName in globalProperties.Keys)
            {
                ProjectCollection.GlobalProjectCollection.RemoveGlobalProperty(propertyName);
            }

            Assert.AreEqual(0, ProjectCollection.GlobalProjectCollection.GlobalProperties.Count);
        }

        /// <summary>
        /// Since when the project file is saved it may be intented we want to make sure the indent charachters do not affect the evaluation against empty. 
        /// We test here newline, tab, and carriage return.
        /// </summary>
        [Test]
        [Category("serialize")]
        public void VerifyNewLinesAndTabsEvaluateToEmpty()
        {
            MockLogger mockLogger = new MockLogger();

            string projectFileContent = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace'>
                       <PropertyGroup><NewLine>" + Environment.NewLine + Environment.NewLine + "</NewLine></PropertyGroup>" +
                       "<PropertyGroup><Tab>\t\t\t\t</Tab></PropertyGroup>" +
                       "<PropertyGroup><CarriageReturn>\r\r\r\r</CarriageReturn></PropertyGroup>" +
                        @"<PropertyGroup><Message1 Condition =""'$(NewLine)' == ''"">NewLineEvalAsEmpty</Message1></PropertyGroup>
                        <PropertyGroup><Message2 Condition =""'$(Tab)' == ''"">TabEvalAsEmpty</Message2></PropertyGroup>
                        <PropertyGroup><Message3 Condition =""'$(CarriageReturn)' == ''"">CarriageReturnEvalAsEmpty</Message3></PropertyGroup>

                        <Target Name=""BUild"">
                           <Message Text=""$(Message1)"" Importance=""High""/>
                          <Message Text=""$(Message2)"" Importance=""High""/>
                          <Message Text=""$(Message3)"" Importance=""High""/>
                       </Target>                    
                    </Project>");

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(projectFileContent)));
            Project project = new Project(xml);
            bool result = project.Build(new ILogger[] { mockLogger });
            Assert.AreEqual(true, result);
            mockLogger.AssertLogContains("NewLineEvalAsEmpty");
            mockLogger.AssertLogContains("TabEvalAsEmpty");
            mockLogger.AssertLogContains("CarriageReturnEvalAsEmpty");
        }

        /// <summary>
        /// Make sure if we build a project and specify no loggers that the loggers registered on the project collection is the one used.
        /// </summary>
        [Test]
        [Category("serialize")]
        public void LogWithLoggersOnProjectCollection()
        {
            MockLogger mockLogger = new MockLogger();

            string projectFileContent = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace'>
                      <Target Name=""BUild"">
                           <Message Text=""IHaveBeenLogged"" Importance=""High""/>
                       </Target>                    
                    </Project>");

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(projectFileContent)));
            ProjectCollection collection = new ProjectCollection();
            collection.RegisterLogger(mockLogger);
            Project project = new Project(xml, null, null, collection);

            bool result = project.Build();
            Assert.AreEqual(true, result);
            mockLogger.AssertLogContains("IHaveBeenLogged");
        }

        /// <summary>
        /// Make sure if we build a project and specify we specify a custom logger that the custom logger is used instead of the one registered on the project collection.
        /// </summary>
        [Test]
        [Category("serialize")]
        public void LogWithLoggersOnProjectCollectionCustomOneUsed()
        {
            MockLogger mockLogger = new MockLogger();
            MockLogger mockLogger2 = new MockLogger();

            string projectFileContent = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace'>
                      <Target Name=""BUild"">
                           <Message Text=""IHaveBeenLogged"" Importance=""High""/>
                       </Target>                    
                    </Project>");

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(projectFileContent)));
            ProjectCollection collection = new ProjectCollection();
            collection.RegisterLogger(mockLogger2);
            Project project = new Project(xml, null, null, collection);

            bool result = project.Build(mockLogger);
            Assert.AreEqual(true, result);
            mockLogger.AssertLogContains("IHaveBeenLogged");
            mockLogger2.AssertLogDoesntContain("IHaveBeenLogged");
        }

        /// <summary>
        /// Load a project from a file path
        /// </summary>
        [Test]
        public void BasicFromFile()
        {
            string file = null;

            try
            {
                file = FileUtilities.GetTemporaryFile();

                string content = GetSampleProjectContent();
                File.WriteAllText(file, content);

                Project project = new Project(file);

                VerifyContentOfSampleProject(project);
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Load a project from a file path that has valid XML that does not 
        /// evaluate successfully; then trying again after fixing the file should succeed.
        /// </summary>
        [Test]
        public void FailedEvaluationClearsXmlCache()
        {
            string file = Path.GetTempPath() + Path.DirectorySeparatorChar + Guid.NewGuid().ToString("N");

            try
            {
                var xml = ProjectRootElement.Create(file);
                xml.AddItem("i", "i1").Condition = "typo in ''condition''";
                xml.Save();

                Project project = null;
                try
                {
                    project = new Project(file);
                }
                catch (InvalidProjectFileException ex)
                {
                    Console.WriteLine(ex.Message);
                }

                // Verify that we don't now have invalid project XML left in the cache
                // by writing out valid project XML and trying again;
                // Don't save through the OM or the cache would get updated; do it directly
                File.WriteAllText(file, ObjectModelHelpers.CleanupFileContents(@"<Project xmlns='msbuildnamespace'/>"));

                project = new Project(file); // should not throw
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Reading from an XMLReader that has no content should throw the correct
        /// exception
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ReadFromEmptyReader1()
        {
            XmlReader reader = XmlReader.Create(new StringReader(String.Empty));
            ProjectRootElement xml = ProjectRootElement.Create(reader);
        }

        /// <summary>
        /// Reading from an XMLReader that has no content should throw the correct
        /// exception
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ReadFromEmptyReader2()
        {
            XmlReader reader = XmlReader.Create(new StringReader(string.Empty));
            Project project = new Project(reader);
        }

        /// <summary>
        /// Reading from an XMLReader that has no content should throw the correct
        /// exception
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ReadFromEmptyReader3()
        {
            // Variation, we have a reader but it's already read
            XmlReader reader = XmlReader.Create(new StringReader(ProjectRootElement.Create().RawXml));

            while (reader.Read())
            {
            }

            Project project = (new ProjectCollection()).LoadProject(reader);
        }

        /// <summary>
        /// Reading from an XMLReader that was closed should throw the correct
        /// exception
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ReadFromClosedReader()
        {
            XmlReader reader = XmlReader.Create(new StringReader(string.Empty));
            reader.Close();
            Project project = new Project(reader);
        }

        /// <summary>
        /// Reading from an XMLReader that has TWO valid root elements should work
        /// if it's already read past the first one.
        /// </summary>
        [Test]
        public void ReadFromReaderTwoDocs()
        {
            string emptyProject = ObjectModelHelpers.CleanupFileContents(@"<Project ToolsVersion=""msbuilddefaulttoolsversion"" xmlns=""msbuildnamespace""/>");
            XmlReader reader = XmlReader.Create(new StringReader(emptyProject + emptyProject), new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });

            reader.ReadToFollowing("Project");
            reader.Read();

            Project project2 = new Project(reader);

            Assert.AreEqual(false, reader.Read());
        }

        /// <summary>
        /// Import does not exist. Default case is an exception.
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ImportDoesNotExistDefaultSettings()
        {
            ProjectRootElement xml = ProjectRootElement.Create();
            xml.AddImport("__nonexistent__");

            Project project = new Project(xml);
        }

        /// <summary>
        /// Import gives invalid uri exception
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ImportInvalidUriFormat()
        {
            ProjectRootElement xml = ProjectRootElement.Create();
            xml.AddImport(@"//MSBuildExtensionsPath32)\4.0\Microsoft.VisualStudioVersion.v11.Common.props");

            Project project = new Project(xml);
        }

        /// <summary>
        /// Necessary but not sufficient for MSBuild evaluation to be thread safe.
        /// </summary>
        [Test]
        public void ConcurrentLoadDoesNotCrash()
        {
            var tasks = new Task[500];

            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Factory.StartNew(delegate () { new Project(); }); // Should not throw
            }

            Task.WaitAll(tasks);
        }

        /// <summary>
        /// Import does not exist but ProjectLoadSettings.IgnoreMissingImports was set
        /// </summary>
        [Test]
        public void ImportDoesNotExistIgnoreMissingImports()
        {
            ProjectRootElement xml = ProjectRootElement.Create();

            xml.AddProperty("p", "1");
            xml.AddImport("__nonexistent__");
            xml.AddProperty("q", "$(p)");

            Project project = new Project(xml, null, null, new ProjectCollection(), ProjectLoadSettings.IgnoreMissingImports);

            // Make sure some evaluation did occur
            Assert.AreEqual("1", project.GetPropertyValue("q"));
        }

        /// <summary>
        /// When we try and access the ImportsIncludingDuplicates property on the project without setting 
        /// the correct projectloadsetting flag, we expect an invalidoperationexception.
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidOperationException))]
        public void TryImportsIncludingDuplicatesExpectException()
        {
            ProjectRootElement xml = ProjectRootElement.Create();
            Project project = new Project(xml, null, null, new ProjectCollection(), ProjectLoadSettings.IgnoreMissingImports);
            IList<ResolvedImport> imports = project.ImportsIncludingDuplicates;
            Assert.AreEqual(0, imports.Count);
        }

        /// <summary>
        /// Import self ignored
        /// </summary>
        [Test]
        public void ImportSelfIgnored()
        {
            string file = null;

            try
            {
                ProjectCollection collection = new ProjectCollection();
                MockLogger logger = new MockLogger();
                collection.RegisterLogger(logger);

                Project project = new Project(collection);
                project.Xml.AddImport("$(MSBuildProjectFullPath)");

                file = FileUtilities.GetTemporaryFile();
                project.Save(file);
                project.ReevaluateIfNecessary();

                logger.AssertLogContains("MSB4210"); // selfimport
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Import self indirectly ignored
        /// </summary>
        [Test]
        public void ImportSelfIndirectIgnored()
        {
            string file = null;
            string file2 = null;

            try
            {
                ProjectCollection collection = new ProjectCollection();
                MockLogger logger = new MockLogger();
                collection.RegisterLogger(logger);

                file = FileUtilities.GetTemporaryFile();
                file2 = FileUtilities.GetTemporaryFile();
                Project project = new Project(collection);
                project.Xml.AddImport(file2);
                project.Save(file);

                Project project2 = new Project(collection);
                project2.Xml.AddImport(file);
                project2.Save(file2);

                project.ReevaluateIfNecessary();

                logger.AssertLogContains("MSB4210"); // selfimport
            }
            finally
            {
                File.Delete(file);
                File.Delete(file2);
            }
        }

        /// <summary>
        /// Double import ignored
        /// </summary>
        [Test]
        public void DoubleImportIgnored()
        {
            string file = null;
            string file2 = null;

            try
            {
                ProjectCollection collection = new ProjectCollection();
                MockLogger logger = new MockLogger();
                collection.RegisterLogger(logger);

                file = FileUtilities.GetTemporaryFile();
                file2 = FileUtilities.GetTemporaryFile();
                Project project = new Project(collection);
                project.Xml.AddImport(file2);
                project.Xml.AddImport(file2);
                project.Save(file);

                Project project2 = new Project(collection);
                project2.Save(file2);

                project.ReevaluateIfNecessary();

                logger.AssertLogContains("MSB4011"); // duplicate import
            }
            finally
            {
                File.Delete(file);
                File.Delete(file2);
            }
        }

        /// <summary>
        /// Double import ignored
        /// </summary>
        [Test]
        public void DoubleImportIndirectIgnored()
        {
            string file = null;
            string file2 = null;
            string file3 = null;

            try
            {
                ProjectCollection collection = new ProjectCollection();
                MockLogger logger = new MockLogger();
                collection.RegisterLogger(logger);

                file = FileUtilities.GetTemporaryFile();
                file2 = FileUtilities.GetTemporaryFile();
                file3 = FileUtilities.GetTemporaryFile();

                Project project = new Project(collection);
                project.Xml.AddImport(file2);
                project.Xml.AddImport(file3);
                project.Save(file);

                Project project2 = new Project(collection);
                project.Xml.AddImport(file3);
                project2.Save(file2);

                Project project3 = new Project(collection);
                project3.Save(file3);

                project.ReevaluateIfNecessary();

                logger.AssertLogContains("MSB4011"); // duplicate import
            }
            finally
            {
                File.Delete(file);
                File.Delete(file2);
                File.Delete(file3);
            }
        }

        /// <summary>
        /// Basic created from backing XML
        /// </summary>
        [Test]
        public void BasicFromXml()
        {
            ProjectRootElement xml = GetSampleProjectRootElement();
            Project project = new Project(xml);

            VerifyContentOfSampleProject(project);
        }

        /// <summary>
        /// Test Project from an XML with an import.
        /// Also verify the Imports collection on the evaluated Project.
        /// </summary>
        [Test]
        public void BasicFromXmlFollowImport()
        {
            string importContent = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace'>
                        <PropertyGroup>
                            <p2>v3</p2>
                        </PropertyGroup>
                        <ItemGroup>
                            <i Include='i4'/>
                        </ItemGroup>
                        <Target Name='t2'>
                            <task/>
                        </Target>
                    </Project>");

            string importPath = ObjectModelHelpers.CreateFileInTempProjectDirectory("import.targets", importContent);

            string projectFileContent = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace'>
                        <PropertyGroup Condition=""'$(Configuration)'=='Foo'"">
                            <p>v1</p>
                        </PropertyGroup>
                        <PropertyGroup Condition=""'$(Configuration)'!='Foo'"">
                            <p>v2</p>
                        </PropertyGroup>
                        <PropertyGroup>
                            <p2>X$(p)</p2>
                        </PropertyGroup>
                        <ItemGroup>
                            <i Condition=""'$(Configuration)'=='Foo'"" Include='i0'/>
                            <i Include='i1'/>
                            <i Include='$(p)X;i3'/>
                        </ItemGroup>
                        <Target Name='t'>
                            <task/>
                        </Target>
                        <Import Project='{0}'/>
                    </Project>");

            projectFileContent = string.Format(projectFileContent, importPath);

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(projectFileContent)));
            Project project = new Project(xml);

            Assert.AreEqual("v3", project.GetPropertyValue("p2"));

            List<ProjectItem> items = Helpers.MakeList(project.GetItems("i"));
            Assert.AreEqual(4, items.Count);
            Assert.AreEqual("i1", items[0].EvaluatedInclude);
            Assert.AreEqual("v2X", items[1].EvaluatedInclude);
            Assert.AreEqual("i3", items[2].EvaluatedInclude);
            Assert.AreEqual("i4", items[3].EvaluatedInclude);

            IList<ResolvedImport> imports = project.Imports;
            Assert.AreEqual(1, imports.Count);
            Assert.AreEqual(true, object.ReferenceEquals(imports.First().ImportingElement, xml.Imports.ElementAt(0)));

            // We can take advantage of the fact that we will get the same ProjectRootElement from the cache if we try to
            // open it with a path; get that and then compare it to what project.Imports gave us.
            Assert.AreEqual(true, object.ReferenceEquals(imports.First().ImportedProject, ProjectRootElement.Open(importPath)));

            // Test the logical project iterator
            List<ProjectElement> logicalElements = new List<ProjectElement>(project.GetLogicalProject());

            Assert.AreEqual(18, logicalElements.Count);

            ObjectModelHelpers.DeleteTempProjectDirectory();
        }

        /// <summary>
        /// Get items, transforms use correct directory base, ie., the project folder
        /// </summary>
        [Test]
        public void TransformsUseCorrectDirectory_Basic()
        {
            string file = null;

            string projectFileContent = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace'>
                        <ItemGroup>
                            <IntermediateAssembly Include='obj\i386\foo.dll'/>
                            <BuiltProjectOutputGroupKeyOutput Include=""@(IntermediateAssembly->'%(FullPath)')""/>
                        </ItemGroup>
                    </Project>");

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(projectFileContent)));

            Project project = new Project(xml);

            try
            {
                file = FileUtilities.GetTemporaryFile();
                project.Save(file);
                project.ReevaluateIfNecessary();

                Assert.AreEqual(
                    NativeMethodsShared.IsWindows
                        ? Path.Combine(Path.GetTempPath(), @"obj\i386\foo.dll")
                        : Path.Combine(Path.GetTempPath(), @"obj/i386/foo.dll"),
                    project.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Get items, transforms use correct directory base, ie., the current
        /// directory at the time of load for a project that was not yet saved
        /// </summary>
        [Test]
        public void TransformsUseCorrectDirectory_Basic_NotSaved()
        {
            string projectFileContent = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace'>
                        <ItemGroup>
                            <IntermediateAssembly Include='obj\i386\foo.dll'/>
                            <BuiltProjectOutputGroupKeyOutput Include=""@(IntermediateAssembly->'%(FullPath)')""/>
                        </ItemGroup>
                    </Project>");

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(projectFileContent)));

            Project project = new Project(xml);
            ProjectInstance projectInstance = new ProjectInstance(xml);

            if (NativeMethodsShared.IsWindows)
            {
                Assert.AreEqual(
                    Path.Combine(Environment.CurrentDirectory, @"obj\i386\foo.dll"),
                    project.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                Assert.AreEqual(
                    Path.Combine(Environment.CurrentDirectory, @"obj\i386\foo.dll"),
                    projectInstance.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
            }
            else
            {
                Assert.AreEqual(
                    Path.Combine(Environment.CurrentDirectory, @"obj/i386/foo.dll"),
                    project.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                Assert.AreEqual(
                    Path.Combine(Environment.CurrentDirectory, @"obj/i386/foo.dll"),
                    projectInstance.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
            }
        }

        /// <summary>
        /// Directory transform uses project directory
        /// </summary>
        [Test]
        public void TransformsUseCorrectDirectory_DirectoryTransform()
        {
            string file = null;

            string projectFileContent = ObjectModelHelpers.CleanupFileContents(@"<Project xmlns='msbuildnamespace'>
                        <ItemGroup>
                            <IntermediateAssembly Include='obj\i386\foo.dll'/>
                            <BuiltProjectOutputGroupKeyOutput Include=""@(IntermediateAssembly->'%(Directory)')""/>
                        </ItemGroup>
                    </Project>");

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(projectFileContent)));

            try
            {
                file = FileUtilities.GetTemporaryFile();
                xml.FullPath = file;

                Project project = new Project(xml);
                ProjectInstance projectInstance = new ProjectInstance(xml);

                if (NativeMethodsShared.IsWindows)
                {
                    Assert.AreEqual(
                        Path.Combine(Path.GetTempPath().Substring(3) /* remove c:\ */, @"obj\i386\"),
                        project.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                    Assert.AreEqual(
                        Path.Combine(Path.GetTempPath().Substring(3) /* remove c:\ */, @"obj\i386\"),
                        projectInstance.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                }
                else
                {
                    Assert.AreEqual(
                        Path.Combine(Path.GetTempPath(), @"obj/i386/"),
                        project.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                    Assert.AreEqual(
                        Path.Combine(Path.GetTempPath(), @"obj/i386/"),
                        projectInstance.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Directory item function uses project directory
        /// </summary>
        [Test]
        public void TransformsUseCorrectDirectory_DirectoryItemFunction()
        {
            string file = null;

            string projectFileContent = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace'>
                        <ItemGroup>
                            <IntermediateAssembly Include='obj\i386\foo.dll'/>
                            <BuiltProjectOutputGroupKeyOutput Include=""@(IntermediateAssembly->Directory())""/>
                        </ItemGroup>
                    </Project>");

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(projectFileContent)));

            try
            {
                file = FileUtilities.GetTemporaryFile();
                xml.FullPath = file;

                Project project = new Project(xml);
                ProjectInstance projectInstance = new ProjectInstance(xml);

                if (NativeMethodsShared.IsWindows)
                {
                    Assert.AreEqual(
                        Path.Combine(Path.GetTempPath().Substring(3) /* remove c:\ */, @"obj\i386\"),
                        project.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                    Assert.AreEqual(
                        Path.Combine(Path.GetTempPath().Substring(3) /* remove c:\ */, @"obj\i386\"),
                        projectInstance.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                }
                else
                {
                    Assert.AreEqual(
                        Path.Combine(Path.GetTempPath(), @"obj/i386/"),
                        project.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                    Assert.AreEqual(
                        Path.Combine(Path.GetTempPath(), @"obj/i386/"),
                        projectInstance.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                }
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Directory item function uses project directory
        /// </summary>
        [Test]
        public void TransformsUseCorrectDirectory_DirectoryNameItemFunction()
        {
            string file = null;

            string projectFileContent = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace'>
                        <ItemGroup>
                            <IntermediateAssembly Include='obj" + Path.DirectorySeparatorChar + "i386"
                                                                               + Path.DirectorySeparatorChar
                                                                               + @"foo.dll'/>
                            <BuiltProjectOutputGroupKeyOutput Include=""@(IntermediateAssembly->DirectoryName())""/>
                        </ItemGroup>
                    </Project>");

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(projectFileContent)));

            try
            {
                file = FileUtilities.GetTemporaryFile();
                xml.FullPath = file;

                Project project = new Project(xml);
                ProjectInstance projectInstance = new ProjectInstance(xml);

                // Should be the full path to the directory
                Assert.AreEqual(
                    Path.Combine(Path.GetTempPath() /* remove c:\ */, "obj" + Path.DirectorySeparatorChar + "i386"),
                    project.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
                Assert.AreEqual(
                    Path.Combine(Path.GetTempPath() /* remove c:\ */, "obj" + Path.DirectorySeparatorChar + "i386"),
                    projectInstance.GetItems("BuiltProjectOutputGroupKeyOutput").First().EvaluatedInclude);
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// Global properties accessor
        /// </summary>
        [Test]
        public void GetGlobalProperties()
        {
            ProjectRootElement xml = GetSampleProjectRootElement();
            var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("g1", "v1");
            globalProperties.Add("g2", "v2");
            Project project = new Project(xml, globalProperties, null);

            Assert.AreEqual("v1", project.GlobalProperties["g1"]);
            Assert.AreEqual("v2", project.GlobalProperties["g2"]);
        }

        /// <summary>
        /// Global properties are cloned when passed in:
        /// subsequent changes have no effect
        /// </summary>
        [Test]
        public void GlobalPropertiesCloned()
        {
            ProjectRootElement xml = GetSampleProjectRootElement();
            var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("g1", "v1");
            Project project = new Project(xml, globalProperties, null);

            globalProperties.Add("g2", "v2");

            Assert.AreEqual("v1", project.GlobalProperties["g1"]);
            Assert.AreEqual(false, project.GlobalProperties.ContainsKey("g2"));
        }

        /// <summary>
        /// Global properties accessor when no global properties
        /// </summary>
        [Test]
        public void GetGlobalPropertiesNone()
        {
            ProjectRootElement xml = GetSampleProjectRootElement();
            Project project = new Project(xml);

            Assert.AreEqual(0, project.GlobalProperties.Count);
        }

        /// <summary>
        /// Changing global properties should make the project a candidate
        /// for reevaluation.
        /// </summary>
        [Test]
        public void ChangeGlobalProperties()
        {
            Project project = new Project();
            ProjectPropertyElement propertyElement = project.Xml.AddProperty("p", "v0");
            propertyElement.Condition = "'$(g)'=='v1'";
            project.ReevaluateIfNecessary();
            Assert.AreEqual(string.Empty, project.GetPropertyValue("p"));

            Assert.AreEqual(true, project.SetGlobalProperty("g", "v1"));
            Assert.AreEqual(true, project.IsDirty);
            project.ReevaluateIfNecessary();
            Assert.AreEqual("v0", project.GetPropertyValue("p"));
            Assert.AreEqual("v1", project.GlobalProperties["g"]);
        }

        /// <summary>
        /// Changing global property after reevaluation should not crash
        /// </summary>
        [Test]
        public void ChangeGlobalPropertyAfterReevaluation()
        {
            Project project = new Project();
            project.SetGlobalProperty("p", "v1");
            project.ReevaluateIfNecessary();
            project.SetGlobalProperty("p", "v2");

            Assert.AreEqual("v2", project.GetPropertyValue("p"));
            Assert.AreEqual(true, project.GetProperty("p").IsGlobalProperty);
        }

        /// <summary>
        /// Test the SkipEvaluation functionality of ReevaluateIfNecessary
        /// </summary>
        [Test]
        public void SkipEvaluation()
        {
            Project project = new Project();
            project.SetGlobalProperty("p", "v1");
            project.ReevaluateIfNecessary();
            Assert.AreEqual("v1", project.GetPropertyValue("p"));

            project.SkipEvaluation = true;
            ProjectPropertyElement propertyElement = project.Xml.AddProperty("p1", "v0");
            propertyElement.Condition = "'$(g)'=='v1'";
            project.SetGlobalProperty("g", "v1");
            project.ReevaluateIfNecessary();
            Assert.AreEqual(string.Empty, project.GetPropertyValue("p1"));

            project.SkipEvaluation = false;
            project.SetGlobalProperty("g", "v1");
            project.ReevaluateIfNecessary();
            Assert.AreEqual("v0", project.GetPropertyValue("p1"));
        }

        /// <summary>
        /// Setting property with same name as global property but after reevaluation should error
        /// because the property is global, not fail with null reference exception
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidOperationException))]
        public void ChangeGlobalPropertyAfterReevaluation2()
        {
            Project project = new Project();
            project.SetGlobalProperty("p", "v1");
            project.ReevaluateIfNecessary();
            project.SetProperty("p", "v2");
        }

        /// <summary>
        /// Setting environment property should create a real property
        /// </summary>
        [Test]
        public void ChangeEnvironmentProperty()
        {
            Project project = new Project();
            project.SetProperty("computername", "v1");

            Assert.AreEqual("v1", project.GetPropertyValue("computername"));
            Assert.AreEqual(true, project.IsDirty);

            project.ReevaluateIfNecessary();

            Assert.AreEqual("v1", project.GetPropertyValue("computername"));
        }

        /// <summary>
        /// Setting a reserved property through the project should error nicely
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidOperationException))]
        public void SetReservedPropertyThroughProject()
        {
            Project project = new Project();
            project.SetProperty("msbuildprojectdirectory", "v1");
        }

        /// <summary>
        /// Changing global properties with some preexisting.
        /// </summary>
        [Test]
        public void ChangeGlobalPropertiesPreexisting()
        {
            Dictionary<string, string> initial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            initial.Add("p0", "v0");
            initial.Add("p1", "v1");
            Project project = new Project(ProjectRootElement.Create(), initial, null);
            ProjectPropertyElement propertyElement = project.Xml.AddProperty("pp", "vv");
            propertyElement.Condition = "'$(p0)'=='v0' and '$(p1)'=='v1b'";
            project.ReevaluateIfNecessary();
            Assert.AreEqual(string.Empty, project.GetPropertyValue("pp"));

            project.SetGlobalProperty("p1", "v1b");
            Assert.AreEqual(true, project.IsDirty);
            project.ReevaluateIfNecessary();
            Assert.AreEqual("vv", project.GetPropertyValue("pp"));
            Assert.AreEqual("v0", project.GlobalProperties["p0"]);
            Assert.AreEqual("v1b", project.GlobalProperties["p1"]);
        }

        /// <summary>
        /// Changing global properties with some preexisting from the project collection.
        /// Should not modify those on the project collection.
        /// </summary>
        [Test]
        public void ChangeGlobalPropertiesInitiallyFromProjectCollection()
        {
            Dictionary<string, string> initial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            initial.Add("p0", "v0");
            initial.Add("p1", "v1");
            ProjectCollection collection = new ProjectCollection(initial, null, ToolsetDefinitionLocations.Registry);
            Project project = new Project(collection);
            ProjectPropertyElement propertyElement = project.Xml.AddProperty("pp", "vv");
            propertyElement.Condition = "'$(p0)'=='v0' and '$(p1)'=='v1b'";
            project.ReevaluateIfNecessary();
            Assert.AreEqual(string.Empty, project.GetPropertyValue("pp"));

            project.SetGlobalProperty("p1", "v1b");
            Assert.AreEqual(true, project.IsDirty);
            project.ReevaluateIfNecessary();
            Assert.AreEqual("vv", project.GetPropertyValue("pp"));
            Assert.AreEqual("v0", collection.GlobalProperties["p0"]);
            Assert.AreEqual("v1", collection.GlobalProperties["p1"]);
        }

        /// <summary>
        /// Changing global property to the same value should not dirty the project.
        /// </summary>
        [Test]
        public void ChangeGlobalPropertiesSameValue()
        {
            Project project = new Project();
            project.SetGlobalProperty("g", "v1");
            Assert.AreEqual(true, project.IsDirty);
            project.ReevaluateIfNecessary();

            Assert.AreEqual(false, project.SetGlobalProperty("g", "v1"));
            Assert.AreEqual(false, project.IsDirty);
        }

        /// <summary>
        /// Removing global properties should make the project a candidate
        /// for reevaluation.
        /// </summary>
        [Test]
        public void RemoveGlobalProperties()
        {
            Project project = new Project();
            ProjectPropertyElement propertyElement = project.Xml.AddProperty("p", "v0");
            propertyElement.Condition = "'$(g)'==''";
            project.SetGlobalProperty("g", "v1");
            project.ReevaluateIfNecessary();
            Assert.AreEqual(string.Empty, project.GetPropertyValue("p"));

            bool existed = project.RemoveGlobalProperty("g");
            Assert.AreEqual(true, existed);
            Assert.AreEqual(true, project.IsDirty);
            project.ReevaluateIfNecessary();
            Assert.AreEqual("v0", project.GetPropertyValue("p"));
            Assert.AreEqual(false, project.GlobalProperties.ContainsKey("g"));
        }

        /// <summary>
        /// Remove nonexistent global property should return false and not dirty the project.
        /// </summary>
        [Test]
        public void RemoveNonExistentGlobalProperties()
        {
            Project project = new Project();
            bool existed = project.RemoveGlobalProperty("x");

            Assert.AreEqual(false, existed);
            Assert.AreEqual(false, project.IsDirty);
        }

        /// <summary>
        /// ToolsVersion accessor for explicitly specified
        /// </summary>
        [Test]
        public void GetToolsVersionExplicitlySpecified()
        {
            if (ToolLocationHelper.GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version35) == null)
            {
                // "Requires 3.5 to be installed"
                return;
            }

            ProjectRootElement xml = GetSampleProjectRootElement();
            Project project = new Project(
                xml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ObjectModelHelpers.MSBuildDefaultToolsVersion);

            Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, project.ToolsVersion);
        }

        /// <summary>
        /// ToolsVersion accessor when none was specified.
        /// Should not return the value on the project element.
        /// </summary>
        [Test]
        public void GetToolsVersionNoneExplicitlySpecified()
        {
            ProjectRootElement xml = ProjectRootElement.Create();
            xml.ToolsVersion = string.Empty;
            xml.AddTarget("t");

            Project project = new Project(xml);

            Assert.AreEqual(string.Empty, project.Xml.ToolsVersion);

            ObjectModelHelpers.DeleteTempProjectDirectory();
        }

        /// <summary>
        /// ToolsVersion defaults to 4.0
        /// </summary>
        [Test]
        public void GetToolsVersionFromProject()
        {
            Project project = new Project();

            Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, project.ToolsVersion);
        }

        /// <summary>
        /// Project.ToolsVersion should be set to ToolsVersion evaluated with,
        /// even if it is subsequently changed on the XML (without reevaluation)
        /// </summary>
        [Test]
        public void ProjectToolsVersion20Present()
        {
            if (ToolLocationHelper.GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version20) == null)
            {
                // "Requires 2.0 to be installed"
                return;
            }

            Project project = new Project();
            project.Xml.ToolsVersion = "2.0";
            project.ReevaluateIfNecessary();

            // ... and after all that, we end up defaulting to the current ToolsVersion instead.  There's a way 
            // to turn this behavior (new in Dev12) off, but it requires setting an environment variable and 
            // clearing some internal state to make sure that the update environment variable is picked up, so 
            // there's not a good way of doing it from these deliberately public OM only tests. 
            Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, project.ToolsVersion);

            project.Xml.ToolsVersion = "4.0";

            // Still defaulting to the current ToolsVersion
            Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, project.ToolsVersion);
        }

        /// <summary>
        /// Project.ToolsVersion should be set to ToolsVersion evaluated with,
        /// even if it is subsequently changed on the XML (without reevaluation)
        /// </summary>
        [Test]
        public void ProjectToolsVersion20NotPresent()
        {
            if (ToolLocationHelper.GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version20) != null)
            {
                // "Requires 2.0 to NOT be installed"
                return;
            }

            Project project = new Project();
            project.Xml.ToolsVersion = "2.0";
            project.ReevaluateIfNecessary();

            Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, project.ToolsVersion);

            project.Xml.ToolsVersion = ObjectModelHelpers.MSBuildDefaultToolsVersion;

            Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, project.ToolsVersion);
        }

        /// <summary>
        /// $(MSBuildToolsVersion) should be set to ToolsVersion evaluated with,
        /// even if it is subsequently changed on the XML (without reevaluation)
        /// </summary>
        [Test]
        public void MSBuildToolsVersionProperty()
        {
            if (ToolLocationHelper.GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version20) == null)
            {
                // "Requires 2.0 to be installed"
                return;
            }

            Project project = new Project();
            project.Xml.ToolsVersion = "2.0";
            project.ReevaluateIfNecessary();

            // ... and after all that, we end up defaulting to the current ToolsVersion instead.  There's a way 
            // to turn this behavior (new in Dev12) off, but it requires setting an environment variable and 
            // clearing some internal state to make sure that the update environment variable is picked up, so 
            // there's not a good way of doing it from these deliberately public OM only tests. 
            Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, project.GetPropertyValue("msbuildtoolsversion"));

            project.Xml.ToolsVersion = "4.0";

            // Still current
            Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, project.GetPropertyValue("msbuildtoolsversion"));

            project.ReevaluateIfNecessary();

            // Still current
            Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, project.GetPropertyValue("msbuildtoolsversion"));
        }

        /// <summary>
        /// $(MSBuildToolsVersion) should be set to ToolsVersion evaluated with,
        /// even if it is subsequently changed on the XML (without reevaluation)
        /// </summary>
        [Test]
        public void MSBuildToolsVersionProperty40()
        {
            Project project = new Project();

            Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, project.GetPropertyValue("msbuildtoolsversion"));
        }

        /// <summary>
        /// It's okay to change ToolsVersion to some apparently bogus value -- the project can be persisted
        /// that way, and maybe later it'll correspond to some known toolset. If the effective ToolsVersion was being
        /// gotten from the attribute, that'll be affected too; and thus might be bogus.
        /// </summary>
        [Test]
        public void ChangingToolsVersionAttributeToUnrecognizedValue()
        {
            Project project = new Project();

            project.Xml.ToolsVersion = "bogus";

            Assert.AreEqual("bogus", project.Xml.ToolsVersion);
        }

        /// <summary>
        /// Test Project's surfacing of the sub-toolset version
        /// </summary>
        [Test]
        public void GetSubToolsetVersion()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVersion");

            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", null);

                Project p = new Project(GetSampleProjectRootElement(), null, ObjectModelHelpers.MSBuildDefaultToolsVersion, new ProjectCollection());

                Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, p.ToolsVersion);

                Toolset t = p.ProjectCollection.GetToolset(ObjectModelHelpers.MSBuildDefaultToolsVersion);

                Assert.AreEqual(t.DefaultSubToolsetVersion, p.SubToolsetVersion);

                Assert.AreEqual(t.DefaultSubToolsetVersion ?? string.Empty, p.GetPropertyValue("VisualStudioVersion"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Test Project's surfacing of the sub-toolset version when it is overridden by a value in the 
        /// environment 
        /// </summary>
        [Test]
        public void GetSubToolsetVersion_FromEnvironment()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVersion");

            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", "ABCD");

                Project p = new Project(GetSampleProjectRootElement(), null, ObjectModelHelpers.MSBuildDefaultToolsVersion, new ProjectCollection());

                Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, p.ToolsVersion);
                Assert.AreEqual("ABCD", p.SubToolsetVersion);
                Assert.AreEqual("ABCD", p.GetPropertyValue("VisualStudioVersion"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Test ProjectInstance's surfacing of the sub-toolset version when it is overridden by a global property
        /// </summary>
        [Test]
        public void GetSubToolsetVersion_FromProjectGlobalProperties()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVersion");

            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", null);

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("VisualStudioVersion", "ABCDE");

                Project p = new Project(GetSampleProjectRootElement(), globalProperties, ObjectModelHelpers.MSBuildDefaultToolsVersion, new ProjectCollection());

                Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, p.ToolsVersion);
                Assert.AreEqual("ABCDE", p.SubToolsetVersion);
                Assert.AreEqual("ABCDE", p.GetPropertyValue("VisualStudioVersion"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Verify that if a sub-toolset version is passed to the constructor, it all other heuristic methods for 
        /// getting the sub-toolset version. 
        /// </summary>
        [Test]
        public void GetSubToolsetVersion_FromConstructor()
        {
            string originalVisualStudioVersion = Environment.GetEnvironmentVariable("VisualStudioVersion");

            try
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", "ABC");

                IDictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                globalProperties.Add("VisualStudioVersion", "ABCD");

                IDictionary<string, string> projectCollectionGlobalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                projectCollectionGlobalProperties.Add("VisualStudioVersion", "ABCDE");

                Project p = new Project(GetSampleProjectRootElement(), globalProperties, ObjectModelHelpers.MSBuildDefaultToolsVersion, "ABCDEF", new ProjectCollection(projectCollectionGlobalProperties), ProjectLoadSettings.Default);

                Assert.AreEqual(ObjectModelHelpers.MSBuildDefaultToolsVersion, p.ToolsVersion);
                Assert.AreEqual("ABCDEF", p.SubToolsetVersion);
                Assert.AreEqual("ABCDEF", p.GetPropertyValue("VisualStudioVersion"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("VisualStudioVersion", originalVisualStudioVersion);
            }
        }

        /// <summary>
        /// Reevaluation should update the evaluation counter.
        /// </summary>
        [Test]
        public void ReevaluationCounter()
        {
            Project project = new Project();
            int last = project.EvaluationCounter;

            project.ReevaluateIfNecessary();
            Assert.IsTrue(project.EvaluationCounter == last);
            last = project.EvaluationCounter;

            project.SetProperty("p", "v");
            project.ReevaluateIfNecessary();
            Assert.IsTrue(project.EvaluationCounter != last);
        }

        /// <summary>
        /// Unload should not reset the evaluation counter.
        /// </summary>
        [Test]
        public void ReevaluationCounterUnload()
        {
            string path = null;

            try
            {
                path = FileUtilities.GetTemporaryFile();
                ProjectRootElement.Create().Save(path);

                Project project = new Project(path);
                int last = project.EvaluationCounter;

                project.ProjectCollection.UnloadAllProjects();

                project = new Project(path);
                Assert.IsTrue(project.EvaluationCounter != last);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// Modifying the XML of an imported file should cause the project
        /// to be dirtied.
        /// </summary>
        [Test]
        public void ImportedXmlModified()
        {
            string path = null;

            try
            {
                path = FileUtilities.GetTemporaryFile();
                ProjectRootElement import = ProjectRootElement.Create(path);
                import.Save();

                Project project = new Project();
                int last = project.EvaluationCounter;

                project.Xml.AddImport(path);
                project.ReevaluateIfNecessary();
                Assert.IsTrue(project.EvaluationCounter != last);
                last = project.EvaluationCounter;

                project.ReevaluateIfNecessary();
                Assert.IsTrue(project.EvaluationCounter == last);

                import.AddProperty("p", "v");
                Assert.AreEqual(true, project.IsDirty);
                project.ReevaluateIfNecessary();
                Assert.IsTrue(project.EvaluationCounter != last);
                last = project.EvaluationCounter;
                Assert.AreEqual("v", project.GetPropertyValue("p"));

                project.ReevaluateIfNecessary();
                Assert.IsTrue(project.EvaluationCounter == last);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// To support certain corner cases, it is possible to explicitly mark a Project
        /// as dirty, so that reevaluate is productive.
        /// </summary>
        [Test]
        public void ExternallyMarkDirty()
        {
            Project project = new Project();
            project.SetProperty("p", "v");
            project.ReevaluateIfNecessary();

            Assert.AreEqual(false, project.IsDirty);

            ProjectProperty property1 = project.GetProperty("p");

            project.MarkDirty();

            Assert.AreEqual(true, project.IsDirty);

            project.ReevaluateIfNecessary();

            Assert.AreEqual(false, project.IsDirty);

            ProjectProperty property2 = project.GetProperty("p");

            Assert.AreEqual(false, object.ReferenceEquals(property1, property2)); // different object indicates reevaluation occurred
        }

        /// <summary>
        /// Basic test of getting items by their include
        /// </summary>
        [Test]
        public void ItemsByEvaluatedInclude()
        {
            Project project = new Project();
            project.Xml.AddItem("i", "i1");
            project.Xml.AddItem("i", "i1");
            project.Xml.AddItem("j", "j1");
            project.Xml.AddItem("j", "i1");

            project.ReevaluateIfNecessary();

            List<ProjectItem> items = Helpers.MakeList(project.GetItemsByEvaluatedInclude("i1"));

            Assert.AreEqual(3, items.Count);
            foreach (ProjectItem item in items)
            {
                Assert.AreEqual("i1", item.EvaluatedInclude);
            }
        }

        /// <summary>
        /// Basic test of getting items by their include
        /// </summary>
        [Test]
        public void ItemsByEvaluatedInclude_EvaluatedIncludeNeedsEscaping()
        {
            Project project = new Project();
            project.Xml.AddItem("i", "i%261");
            project.Xml.AddItem("j", "i%25261");
            project.Xml.AddItem("k", "j1");
            project.Xml.AddItem("l", "i&1");

            project.ReevaluateIfNecessary();

            List<ProjectItem> items = Helpers.MakeList(project.GetItemsByEvaluatedInclude("i&1"));

            Assert.AreEqual(2, items.Count);
            foreach (ProjectItem item in items)
            {
                Assert.AreEqual("i&1", item.EvaluatedInclude);
                Assert.IsTrue(
                    string.Equals(item.ItemType, "i", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.ItemType, "l", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Verify none returned when none match
        /// </summary>
        [Test]
        public void ItemsByEvaluatedIncludeNone()
        {
            Project project = new Project();
            project.Xml.AddItem("i", "i1");

            project.ReevaluateIfNecessary();

            List<ProjectItem> items = Helpers.MakeList(project.GetItemsByEvaluatedInclude("i2"));

            Assert.AreEqual(0, items.Count);
        }

        /// <summary>
        /// Tests the tracking of virtual items from the construction to instance model, with the removal of a virtual item. 
        /// </summary>
        [Test]
        public void ItemsByEvaluatedIncludeAndExpansion()
        {
            List<string> filePaths = new List<string>();
            string testFileRoot = null;
            try
            {
                int count = 0;
                testFileRoot = Path.Combine(Path.GetTempPath(), "foodir");
                Directory.CreateDirectory(testFileRoot);
                int maxFiles = 2;
                for (int i = 0; i < maxFiles; i++)
                {
                    string fileName = string.Format("foo{0}.foo", i);
                    string filePath = Path.Combine(testFileRoot, fileName);
                    File.WriteAllText(filePath, string.Empty);
                    filePaths.Add(filePath);
                }

                ProjectRootElement projectConstruction = ProjectRootElement.Create();
                projectConstruction.AddItem("foo", Path.Combine(testFileRoot, "*.foo"));

                count = Helpers.Count(projectConstruction.Items);
                Assert.AreEqual(1, count, "Construction Model");

                Project project = new Project(projectConstruction);

                count = Helpers.Count(project.GetItems("foo"));
                Assert.AreEqual(2, count, "Evaluation Model, Before Removal");

                ProjectItem itemToRemove = null;

                // Get the first item from IEnumerable Collection.
                foreach (ProjectItem item in project.Items)
                {
                    itemToRemove = item;
                    break;
                }

                project.RemoveItem(itemToRemove);
                count = Helpers.Count(project.GetItems("foo"));
                Assert.AreEqual(1, count, "Evaluation Model, After Removal");

                ProjectInstance projectInstance = project.CreateProjectInstance();
                count = Helpers.Count(projectInstance.Items);
                Assert.AreEqual(1, count, "Instance Model");

                // Ensure XML has been updated accordingly on the Evaluation model (projectInstance doesn't back onto XML)
                Assert.IsFalse(project.Xml.RawXml.Contains(itemToRemove.Xml.Include));
                Assert.IsFalse(project.Xml.RawXml.Contains("*.foo"));
            }
            finally
            {
                foreach (string filePathToRemove in filePaths)
                {
                    File.Delete(filePathToRemove);
                }

                Directory.Delete(testFileRoot);
            }
        }

        /// <summary>
        /// Reevaluation should update items-by-evaluated-include
        /// </summary>
        [Test]
        public void ItemsByEvaluatedIncludeReevaluation()
        {
            Project project = new Project();
            project.Xml.AddItem("i", "i1");
            project.ReevaluateIfNecessary();

            List<ProjectItem> items = Helpers.MakeList(project.GetItemsByEvaluatedInclude("i1"));
            Assert.AreEqual(1, items.Count);

            project.Xml.AddItem("j", "i1");
            project.ReevaluateIfNecessary();

            items = Helpers.MakeList(project.GetItemsByEvaluatedInclude("i1"));
            Assert.AreEqual(2, items.Count);
        }

        /// <summary>
        /// Direct adds to the project (ie, not added by evaluation) should update
        /// items-by-evaluated-include
        /// </summary>
        [Test]
        public void ItemsByEvaluatedIncludeDirectAdd()
        {
            Project project = new Project();
            project.AddItem("i", "i1");

            List<ProjectItem> items = Helpers.MakeList(project.GetItemsByEvaluatedInclude("i1"));
            Assert.AreEqual(1, items.Count);
        }

        /// <summary>
        /// Direct removes from the project (ie, not removed by evaluation) should update
        /// items-by-evaluated-include
        /// </summary>
        [Test]
        public void ItemsByEvaluatedIncludeDirectRemove()
        {
            Project project = new Project();
            ProjectItem item1 = project.AddItem("i", "i1;j1")[0];
            project.RemoveItem(item1);

            List<ProjectItem> items = Helpers.MakeList(project.GetItemsByEvaluatedInclude("i1"));
            Assert.AreEqual(0, items.Count);
        }

        /// <summary>
        /// Choose, When has true condition
        /// </summary>
        [Test]
        public void ChooseWhenTrue()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <Choose>
                            <When Condition='true'>
                              <PropertyGroup>
                                <p>v1</p>
                              </PropertyGroup> 
                              <ItemGroup>
                                <i Include='i1' />
                              </ItemGroup>
                            </When>      
                        </Choose>
                    </Project>
                ");

            Project project = new Project(XmlReader.Create(new StringReader(content)));

            Assert.AreEqual("v1", project.GetPropertyValue("p"));
            Assert.AreEqual("i1", Helpers.MakeList(project.GetItems("i"))[0].EvaluatedInclude);
        }

        /// <summary>
        /// Choose, second When has true condition
        /// </summary>
        [Test]
        public void ChooseSecondWhenTrue()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <Choose>
                            <When Condition='false'>
                              <PropertyGroup>
                                <p>v1</p>
                              </PropertyGroup> 
                              <ItemGroup>
                                <i Include='i1' />
                              </ItemGroup>
                            </When>   
                            <When Condition='true'>
                              <PropertyGroup>
                                <p>v2</p>
                              </PropertyGroup> 
                              <ItemGroup>
                                <i Include='i2' />
                              </ItemGroup>
                            </When>    
                        </Choose>
                    </Project>
                ");

            Project project = new Project(XmlReader.Create(new StringReader(content)));

            Assert.AreEqual("v2", project.GetPropertyValue("p"));
            Assert.AreEqual("i2", Helpers.MakeList(project.GetItems("i"))[0].EvaluatedInclude);
        }

        /// <summary>
        /// Choose, when has false condition
        /// </summary>
        [Test]
        public void ChooseOtherwise()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <Choose>
                            <When Condition='false'>
                              <PropertyGroup>
                                <p>v1</p>
                              </PropertyGroup> 
                              <ItemGroup>
                                <i Include='i1' />
                              </ItemGroup>
                            </When>   
                            <Otherwise>
                              <PropertyGroup>
                                <p>v2</p>
                              </PropertyGroup> 
                              <ItemGroup>
                                <i Include='i2' />
                              </ItemGroup>
                            </Otherwise>    
                        </Choose>
                    </Project>
                ");

            Project project = new Project(XmlReader.Create(new StringReader(content)));

            Assert.AreEqual("v2", project.GetPropertyValue("p"));
            Assert.AreEqual("i2", Helpers.MakeList(project.GetItems("i"))[0].EvaluatedInclude);
        }

        /// <summary>
        /// Choose should be entered twice, once for properties and again for items.
        /// That means items should see properties defined below.
        /// </summary>
        [Test]
        public void ChooseTwoPasses()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <Choose>
                            <When Condition='true'>
                              <ItemGroup>
                                <i Include='$(p)_$(p2)' />
                              </ItemGroup>
                              <PropertyGroup>
                                <p>@(i);v1</p>
                              </PropertyGroup> 
                            </When>      
                        </Choose>

                      <PropertyGroup>
                        <p2>v2</p2>
                      </PropertyGroup> 

                        <Choose>
                            <When Condition='false'/>
                            <Otherwise>
                              <ItemGroup>
                                <j Include='$(q)_$(q2)' />
                              </ItemGroup>
                              <PropertyGroup>
                                <q>@(j);v1</q>
                              </PropertyGroup> 
                            </Otherwise>
                        </Choose>

                      <PropertyGroup>
                        <q2>v2</q2>
                      </PropertyGroup> 
                    </Project>
                ");

            Project project = new Project(XmlReader.Create(new StringReader(content)));

            Assert.AreEqual("@(i);v1", project.GetPropertyValue("p"));
            Assert.AreEqual("@(j);v1", project.GetPropertyValue("q"));
            Assert.AreEqual("v1_v2", project.GetItems("i").ElementAt(0).EvaluatedInclude);
            Assert.AreEqual(1, project.GetItems("i").Count());
            Assert.AreEqual("v1_v2", project.GetItems("j").ElementAt(0).EvaluatedInclude);
            Assert.AreEqual(1, project.GetItems("j").Count());
        }

        /// <summary>
        /// Choose conditions are only evaluated once, on the property pass
        /// </summary>
        [Test]
        public void ChooseEvaluateConditionOnlyOnce()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <Choose>
                            <When Condition=""'$(p)' != ''"">
                              <ItemGroup>
                                <i Include='i1' />
                              </ItemGroup>
                            </When>      
                        </Choose>

                      <PropertyGroup>
                        <p>v</p>
                      </PropertyGroup> 

                    </Project>
                ");

            Project project = new Project(XmlReader.Create(new StringReader(content)));

            Assert.AreEqual(0, project.GetItems("i").Count());
        }

        /// <summary>
        /// Choose items can see item definitions below
        /// </summary>
        [Test]
        public void ChooseSeesItemDefinitions()
        {
            string content = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='msbuildnamespace' >
                        <Choose>
                            <When Condition='true'>
                              <ItemGroup>
                                <i Include='i1'>
                                  <m>%(m);m1</m>
                                </i>
                              </ItemGroup>
                            </When>      
                        </Choose>

                      <ItemDefinitionGroup>
                        <i>
                          <m>m0</m>
                        </i>
                      </ItemDefinitionGroup> 

                    </Project>
                ");

            Project project = new Project(XmlReader.Create(new StringReader(content)));

            Assert.AreEqual("m0;m1", project.GetItems("i").ElementAt(0).GetMetadataValue("m"));
        }

        /// <summary>
        /// When build is disabled on the project, it shouldn't run, and should give MSB4112.
        /// </summary>
        [Test]
        public void BuildDisabled()
        {
            Project project = new Project();
            project.Xml.AddTarget("t");
            project.IsBuildEnabled = false;
            MockLogger mockLogger = new MockLogger();
            ProjectCollection.GlobalProjectCollection.RegisterLogger(mockLogger);

            bool result = project.Build();

            Assert.AreEqual(false, result);

            Assert.IsTrue(
                mockLogger.Errors[0].Code == "MSB4112",
                "Security message about disabled targets need to have code MSB4112, because code in the VS Core project system depends on this.  See DesignTimeBuildFeedback.cpp.");
        }

        /// <summary>
        /// Building a nonexistent target should log an error and return false (not throw)
        /// </summary>
        [Test]
        [Category("serialize")]
        public void BuildNonExistentTarget()
        {
            Project project = new Project();
            MockLogger logger = new MockLogger();
            bool result = project.Build(new[] { "nonexistent" }, new List<ILogger>() { logger });
            Assert.AreEqual(false, result);
            Assert.AreEqual(1, logger.ErrorCount);
        }

        /// <summary>
        /// When Project.Build is invoked with custom loggers, those loggers should contain the result of any evaluation warnings and errors.
        /// </summary>
        [Test]
        [Category("serialize")]
        public void BuildEvaluationUsesCustomLoggers()
        {
            string importProjectContent =
                ObjectModelHelpers.CleanupFileContents(@"<Project xmlns='msbuildnamespace'>
                </Project>");

            string importFileName = Microsoft.Build.Shared.FileUtilities.GetTemporaryFile() + ".proj";
            File.WriteAllText(importFileName, importProjectContent);

            string projectContent =
                ObjectModelHelpers.CleanupFileContents(@"<Project xmlns='msbuildnamespace'>
                    <Import Project=""" + importFileName + @"""/>
                    <Import Project=""" + importFileName + @"""/>
                    <ItemGroup>
                        <Compile Include='a.cs' />
                    </ItemGroup>
                    <Target Name=""Build"" />
                </Project>");

            Project project = new Project(XmlReader.Create(new StringReader(projectContent)));
            project.MarkDirty();

            MockLogger collectionLogger = new MockLogger();
            project.ProjectCollection.RegisterLogger(collectionLogger);

            MockLogger mockLogger = new MockLogger();

            bool result;

            try
            {
                result = project.Build(new ILogger[] { mockLogger });
            }
            catch
            {
                throw;
            }
            finally
            {
                project.ProjectCollection.UnregisterAllLoggers();
            }

            Assert.AreEqual(true, result);

            Assert.IsTrue(
                mockLogger.WarningCount == 0,
                "Log should not contain MSB4011 because the build logger will not receive evaluation messages.");

            Assert.IsTrue(
                collectionLogger.Warnings[0].Code == "MSB4011",
                "Log should contain MSB4011 because the project collection logger should have been used for evaluation.");
        }

        /// <summary>
        /// UsingTask expansion should throw InvalidProjectFileException
        /// if it expands to nothing.
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void UsingTaskExpansion1()
        {
            ProjectRootElement xml = ProjectRootElement.Create();
            xml.AddUsingTask("x", "@(x->'%(x)')", null);
            Project project = new Project(xml);
        }

        /// <summary>
        /// UsingTask expansion should throw InvalidProjectFileException
        /// if it expands to nothing.
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void UsingTaskExpansion2()
        {
            ProjectRootElement xml = ProjectRootElement.Create();
            xml.AddUsingTask("@(x->'%(x)')", "y", null);
            Project project = new Project(xml);
        }

        /// <summary>
        /// UsingTask expansion should throw InvalidProjectFileException
        /// if it expands to nothing.
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void UsingTaskExpansion3()
        {
            ProjectRootElement xml = ProjectRootElement.Create();
            xml.AddUsingTask("x", null, "@(x->'%(x)')");
            Project project = new Project(xml);
        }

        /// <summary>
        /// Saving project should make it "clean" for saving
        /// but "dirty" for reevaluation if it was to a new location
        /// </summary>
        [Test]
        public void SavingProjectClearsDirtyBit()
        {
            string contents = ObjectModelHelpers.CleanupFileContents(@"<Project xmlns='msbuildnamespace'/>");
            Project project = new Project(XmlReader.Create(new StringReader(contents)));

            Assert.IsTrue(project.Xml.HasUnsavedChanges); // Not dirty for saving
            Assert.IsFalse(project.IsDirty, "1"); // was evaluated on load

            string file = null;
            try
            {
                file = FileUtilities.GetTemporaryFile();
                project.Save(file);
            }
            finally
            {
                if (file != null)
                {
                    File.Delete(file);
                }
            }

            Assert.IsFalse(project.Xml.HasUnsavedChanges); // Not dirty for saving
            Assert.IsTrue(project.IsDirty, "2"); // Dirty for reevaluation, because the project now has gotten a new file name
        }

        /// <summary>
        /// Remove an already removed item
        /// </summary>
        [Test]
        public void RemoveItemTwiceEvaluationProject()
        {
            string projectOriginalContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' DefaultTargets='Build' xmlns='msbuildnamespace'>
                    <ItemGroup>
                        <Compile Include='a.cs' />
                    </ItemGroup>
                </Project>
                ");
            Project project = new Project(XmlReader.Create(new StringReader(projectOriginalContents)));
            ProjectItem itemToRemove = Helpers.GetFirst(project.GetItems("Compile"));
            project.RemoveItem(itemToRemove);
            project.RemoveItem(itemToRemove); // should not throw

            Assert.AreEqual(0, Helpers.MakeList(project.Items).Count);
        }

        /// <summary>
        /// Remove an updated item
        /// </summary>
        [Test]
        public void RemoveItemOutdatedByUpdate()
        {
            string projectOriginalContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' DefaultTargets='Build' xmlns='msbuildnamespace'>
                    <ItemGroup>
                        <Compile Include='a.cs' />
                    </ItemGroup>
                </Project>
                ");
            Project project = new Project(XmlReader.Create(new StringReader(projectOriginalContents)));
            ProjectItem itemToRemove = Helpers.GetFirst(project.GetItems("Compile"));
            itemToRemove.UnevaluatedInclude = "b.cs";
            project.RemoveItem(itemToRemove); // should not throw

            Assert.AreEqual(0, Helpers.MakeList(project.Items).Count);
        }

        /// <summary>
        /// Remove several items
        /// </summary>
        [Test]
        public void RemoveSeveralItems()
        {
            string projectOriginalContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' DefaultTargets='Build' xmlns='msbuildnamespace'>
                    <ItemGroup>
                        <i Include='i1' />
                        <i Include='i2' />
                    </ItemGroup>
                </Project>
                ");
            Project project = new Project(XmlReader.Create(new StringReader(projectOriginalContents)));

            project.RemoveItems(project.GetItems("i"));

            Assert.AreEqual(0, project.Items.Count());
        }

        /// <summary>
        /// Remove several items
        /// </summary>
        [Test]
        public void RemoveSeveralItemsOfVariousTypes()
        {
            string projectOriginalContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' DefaultTargets='Build' xmlns='msbuildnamespace'>
                    <ItemGroup>
                        <i Include='i1' />
                        <j Include='j1' />
                        <j Include='j2' />
                        <k Include='k1' />
                    </ItemGroup>
                </Project>
                ");
            Project project = new Project(XmlReader.Create(new StringReader(projectOriginalContents)));

            List<ProjectItem> list = new List<ProjectItem>() { project.GetItems("i").FirstOrDefault(), project.GetItems("j").FirstOrDefault() };

            project.RemoveItems(list);

            Assert.AreEqual(2, project.Items.Count());
        }

        /// <summary>
        /// Remove items expanding itemlist expression
        /// </summary>
        [Test]
        public void RemoveSeveralItemsExpandExpression()
        {
            string projectOriginalContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' DefaultTargets='Build' xmlns='msbuildnamespace'>
                    <ItemGroup>
                        <i Include='i1;i2' />
                        <j Include='@(i);j2' />
                    </ItemGroup>
                </Project>
                ");
            Project project = new Project(XmlReader.Create(new StringReader(projectOriginalContents)));

            project.RemoveItems(project.GetItems("j").Take(2));
            Assert.AreEqual(3, project.Items.Count());

            StringWriter writer = new StringWriter();
            project.Save(writer);

            string projectExpectedContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' DefaultTargets='Build' xmlns='msbuildnamespace'>
                    <ItemGroup>
                        <i Include='i1;i2' />
                        <j Include='j2' />
                    </ItemGroup>
                </Project>
                ");

            Helpers.CompareProjectXml(projectExpectedContents, writer.ToString());
        }

        /// <summary>
        /// Remove several items where removing the first one
        /// causes the second one to be detached
        /// </summary>
        [Test]
        public void RemoveSeveralItemsFirstZombiesSecond()
        {
            string projectOriginalContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' DefaultTargets='Build' xmlns='msbuildnamespace'>
                    <ItemGroup>
                        <i Include='i1;i2' />
                    </ItemGroup>
                </Project>
                ");
            Project project = new Project(XmlReader.Create(new StringReader(projectOriginalContents)));

            project.RemoveItems(project.GetItems("i"));

            Assert.AreEqual(0, project.Items.Count());
        }

        /// <summary>
        /// Should not get null reference
        /// </summary>
        [Test]
        [ExpectedException(typeof(ArgumentNullException))]
        public void RemoveItemsOneNull()
        {
            Project project = new Project();
            project.RemoveItems(new List<ProjectItem>() { null });
        }

        /// <summary>
        /// Remove several items where removing the first one
        /// causes the second one to be detached
        /// </summary>
        [Test]
        [ExpectedException(typeof(ArgumentException))]
        public void RemoveItemWrongProject()
        {
            ProjectRootElement root1 = ProjectRootElement.Create();
            root1.AddItem("i", "i1");
            ProjectRootElement root2 = ProjectRootElement.Create();
            root2.AddItem("i", "i1");
            Project project1 = new Project(root1);
            Project project2 = new Project(root2);

            project1.RemoveItems(project2.Items);
        }

        /// <summary>
        /// Remove an item that is no longer attached. For convenience,
        /// we just skip it.
        /// </summary>
        [Test]
        public void RemoveZombiedItem()
        {
            string projectOriginalContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' DefaultTargets='Build' xmlns='msbuildnamespace'>
                    <ItemGroup>
                        <i Include='i1' />
                    </ItemGroup>
                </Project>
                ");
            Project project = new Project(XmlReader.Create(new StringReader(projectOriginalContents)));
            ProjectItem item = project.GetItems("i").FirstOrDefault();

            project.RemoveItems(new List<ProjectItem>() { item });
            project.RemoveItems(new List<ProjectItem>() { item });

            Assert.AreEqual(0, project.Items.Count());
        }

        /// <summary> 
        /// Reserved property in project constructor should just throw
        /// </summary>
        [Test]
        [ExpectedException(typeof(ArgumentException))]
        public void ReservedPropertyProjectConstructor()
        {
            Dictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("msbuildprojectdirectory", "x");

            Project project = new Project(globalProperties, null, new ProjectCollection());
        }

        /// <summary> 
        /// Reserved property in project collection global properties should log an error then rethrow
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ReservedPropertyProjectCollectionConstructor()
        {
            Dictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("msbuildprojectdirectory", "x");
            MockLogger logger = new MockLogger();
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);

            try
            {
                ProjectCollection collection = new ProjectCollection(globalProperties, loggers, ToolsetDefinitionLocations.None);
            }
            finally
            {
                logger.AssertLogContains("MSB4177");
            }
        }

        /// <summary> 
        /// Invalid property (reserved name) in project collection global properties should log an error then rethrow
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ReservedPropertyProjectCollectionConstructor2()
        {
            Dictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Target", "x");
            MockLogger logger = new MockLogger();
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);

            try
            {
                ProjectCollection collection = new ProjectCollection(globalProperties, loggers, ToolsetDefinitionLocations.None);
            }
            finally
            {
                logger.AssertLogContains("MSB4177");
            }
        }

        /// <summary>
        /// Create tree like this
        /// 
        /// \b.targets
        /// \sub\a.proj
        /// 
        /// An item specified with "..\*" in b.targets should find b.targets
        /// as it was evaluated relative to the project file itself.
        /// </summary>
        [Test]
        public void RelativePathsInItemsInTargetsFilesAreRelativeToProjectFile()
        {
            string directory = null;

            try
            {
                directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                string subdirectory = Path.Combine(directory, "sub");
                Directory.CreateDirectory(subdirectory);

                string projectPath = Path.Combine(subdirectory, "a.proj");
                string targetsPath = Path.Combine(directory, "b.targets");

                string unevaluatedInclude = ".." + Path.DirectorySeparatorChar + "*";
                string evaluatedInclude = ".." + Path.DirectorySeparatorChar + "b.targets";

                ProjectRootElement targetsXml = ProjectRootElement.Create(targetsPath);
                targetsXml.AddItem("i", unevaluatedInclude);
                targetsXml.Save();

                ProjectRootElement projectXml = ProjectRootElement.Create(projectPath);
                projectXml.AddImport(evaluatedInclude);
                projectXml.Save();

                Project project = new Project(projectPath);

                IEnumerable<ProjectItem> items = project.GetItems("i");
                Assert.AreEqual(unevaluatedInclude, Helpers.GetFirst(items).UnevaluatedInclude);
                Assert.AreEqual(evaluatedInclude, Helpers.GetFirst(items).EvaluatedInclude);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        /// <summary> 
        /// Invalid property (space) in project collection global properties should log an error then rethrow
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ReservedPropertyProjectCollectionConstructor3()
        {
            Dictionary<string, string> globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            globalProperties.Add("Target", "x");
            MockLogger logger = new MockLogger();
            List<ILogger> loggers = new List<ILogger>();
            loggers.Add(logger);

            try
            {
                ProjectCollection collection = new ProjectCollection(globalProperties, loggers, ToolsetDefinitionLocations.None);
            }
            finally
            {
                logger.AssertLogContains("MSB4177");
            }
        }

        /// <summary>
        /// Create a structure of various imports and verify that project.GetLogicalProject()
        /// walks through them correctly.
        /// </summary>
        [Test]
        public void VariousImports()
        {
            ProjectRootElement one = ProjectRootElement.Create("c:\\1.targets");
            one.AddProperty("p", "1");
            ProjectRootElement two = ProjectRootElement.Create("c:\\2.targets");
            two.AddProperty("p", "2");

            ProjectRootElement zero = ProjectRootElement.Create("c:\\foo\\0.targets");
            zero.AddProperty("p", "0");
            zero.AddImport(one.FullPath);
            zero.AddImport(two.FullPath);
            zero.AddImport(two.FullPath); // Duplicated import: only the first one should be entered
            zero.AddImport(zero.FullPath); // Ignored self import

            ProjectRootElement three = ProjectRootElement.Create("c:\\3.targets");
            three.AddProperty("p", "3");
            one.AddImport(three.FullPath);

            ProjectRootElement four = ProjectRootElement.Create("c:\\4.targets");
            four.AddProperty("p", "4");
            one.AddImport(four.FullPath).Condition = "false"; // False condition; should not be entered

            Project project = new Project(zero);

            List<ProjectElement> logicalProject = new List<ProjectElement>(project.GetLogicalProject());

            Assert.AreEqual(8, logicalProject.Count); // 4 properties + 4 property groups
            Assert.AreEqual(true, object.ReferenceEquals(zero, logicalProject[0].ContainingProject));
            Assert.AreEqual(true, object.ReferenceEquals(zero, logicalProject[1].ContainingProject));
            Assert.AreEqual(true, object.ReferenceEquals(one, logicalProject[2].ContainingProject));
            Assert.AreEqual(true, object.ReferenceEquals(one, logicalProject[3].ContainingProject));
            Assert.AreEqual(true, object.ReferenceEquals(three, logicalProject[4].ContainingProject));
            Assert.AreEqual(true, object.ReferenceEquals(three, logicalProject[5].ContainingProject));
            Assert.AreEqual(true, object.ReferenceEquals(two, logicalProject[6].ContainingProject));
            Assert.AreEqual(true, object.ReferenceEquals(two, logicalProject[7].ContainingProject));

            // Clear the cache
            project.ProjectCollection.UnloadAllProjects();
        }

        /// <summary>
        /// Create a structure containing a import statement such that the import statement results in more than one
        /// file being imported. Then, verify that project.GetLogicalProject() walks through them correctly.
        /// </summary>
        [Test]
        public void LogicalProjectWithWildcardImport()
        {
            string myTempDir = Path.Combine(Path.GetTempPath() + "MyTempDir");

            try
            {
                // Create a new directory in the system temp folder.
                Directory.CreateDirectory(myTempDir);

                ProjectRootElement one = ProjectRootElement.Create(Path.Combine(myTempDir, "1.targets"));
                one.Save();
                one.AddProperty("p", "1");

                ProjectRootElement two = ProjectRootElement.Create(Path.Combine(myTempDir, "2.targets"));
                two.Save();
                two.AddProperty("p", "2");

                ProjectRootElement zero = ProjectRootElement.Create(Path.Combine(myTempDir, "0.targets"));
                zero.AddProperty("p", "0");

                // Add a single import statement that would import both one and two.
                zero.AddImport(Path.Combine(myTempDir, "*.targets"));

                Project project = new Project(zero);

                List<ProjectElement> logicalProject = new List<ProjectElement>(project.GetLogicalProject());

                Assert.AreEqual(6, logicalProject.Count); // 3 properties + 3 property groups
                Assert.AreEqual(true, object.ReferenceEquals(zero, logicalProject[0].ContainingProject)); // PropertyGroup
                Assert.AreEqual(true, object.ReferenceEquals(zero, logicalProject[1].ContainingProject)); // p = 0
                Assert.AreEqual(true, object.ReferenceEquals(one, logicalProject[2].ContainingProject));  // PropertyGroup
                Assert.AreEqual(true, object.ReferenceEquals(one, logicalProject[3].ContainingProject));  // p = 1
                Assert.AreEqual(true, object.ReferenceEquals(two, logicalProject[4].ContainingProject));  // PropertyGroup
                Assert.AreEqual(true, object.ReferenceEquals(two, logicalProject[5].ContainingProject));  // p = 2

                // Clear the cache
                project.ProjectCollection.UnloadAllProjects();
            }
            finally
            {
                // Delete the temp directory that was created above.
                if (Directory.Exists(myTempDir))
                {
                    Directory.Delete(myTempDir, true);
                }
            }
        }

        /// <summary>
        /// Import of string that evaluates to empty should give InvalidProjectFileException
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ImportPropertyEvaluatingToEmpty()
        {
            string projectOriginalContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' DefaultTargets='Build' xmlns='msbuildnamespace'>
                  <Import Project='$(not_defined)'/>
                </Project>
                ");
            Project project = new Project(XmlReader.Create(new StringReader(projectOriginalContents)));
        }

        /// <summary>
        /// Import of string that evaluates to invalid path should cause InvalidProjectFileException
        /// </summary>
        [Test]
        [ExpectedException(typeof(InvalidProjectFileException))]
        public void ImportPropertyEvaluatingToInvalidPath()
        {
            string projectOriginalContents = ObjectModelHelpers.CleanupFileContents(@"
                <Project ToolsVersion='msbuilddefaulttoolsversion' DefaultTargets='Build' xmlns='msbuildnamespace'>
                  <PropertyGroup>
                    <p>|</p>
                  </PropertyGroup>
                  <Import Project='$(p)'/>
                </Project>
                ");
            Project project = new Project(XmlReader.Create(new StringReader(projectOriginalContents)));
        }

        /// <summary>
        /// Creates a simple ProjectRootElement object.
        /// (When ProjectRootElement supports editing, we need not load from a string here.)
        /// </summary>
        private ProjectRootElement GetSampleProjectRootElement()
        {
            string projectFileContent = GetSampleProjectContent();

            ProjectRootElement xml = ProjectRootElement.Create(XmlReader.Create(new StringReader(projectFileContent)));

            return xml;
        }

        /// <summary>
        /// Creates a simple project content.
        /// </summary>
        private string GetSampleProjectContent()
        {
            string projectFileContent = ObjectModelHelpers.CleanupFileContents(@"
                    <Project xmlns='http://schemas.microsoft.com/developer/msbuild/2003' ToolsVersion='2.0' InitialTargets='it' DefaultTargets='dt'>
                        <PropertyGroup Condition=""'$(Configuration)'=='Foo'"">
                            <p>v1</p>
                        </PropertyGroup>
                        <PropertyGroup Condition=""'$(Configuration)'!='Foo'"">
                            <p>v2</p>
                        </PropertyGroup>
                        <PropertyGroup>
                            <p2>X$(p)</p2>
                        </PropertyGroup>
                        <ItemGroup>
                            <i Condition=""'$(Configuration)'=='Foo'"" Include='i0'/>
                            <i Include='i1'/>
                            <i Include='$(p)X;i3'/>
                        </ItemGroup>
                        <Target Name='t'>
                            <task/>
                        </Target>
                    </Project>
                ");

            return projectFileContent;
        }

        /// <summary>
        /// Check the items and properties from the sample project
        /// </summary>
        private void VerifyContentOfSampleProject(Project project)
        {
            Assert.AreEqual("v2", project.GetProperty("p").UnevaluatedValue);
            Assert.AreEqual("Xv2", project.GetProperty("p2").EvaluatedValue);
            Assert.AreEqual("X$(p)", project.GetProperty("p2").UnevaluatedValue);

            IList<ProjectItem> items = Helpers.MakeList(project.GetItems("i"));
            Assert.AreEqual(3, items.Count);
            Assert.AreEqual("i1", items[0].EvaluatedInclude);
            Assert.AreEqual("v2X", items[1].EvaluatedInclude);
            Assert.AreEqual("$(p)X;i3", items[1].UnevaluatedInclude);
            Assert.AreEqual("i3", items[2].EvaluatedInclude);
        }
    }
}
