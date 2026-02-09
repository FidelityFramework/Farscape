namespace Farscape.Core

open System
open System.IO
open System.Text
open ProjectOptions

/// Module for generating F# project files
module Project =
    /// Creates directory if it doesn't exist
    let ensureDirectory (path: string) =
        if not (Directory.Exists(path)) then
            Directory.CreateDirectory(path) |> ignore

    /// Generate a solution file
    let generateSolutionFile (options: ProjectOptions) (solutionPath: string) =
        let libraryProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant()
        let testsProjectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant()
        let solutionGuid = Guid.NewGuid().ToString("B").ToUpperInvariant()
        
        let sb = StringBuilder()
        
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00") |> ignore
        sb.AppendLine("# Visual Studio Version 17") |> ignore
        sb.AppendLine("VisualStudioVersion = 17.0.31903.59") |> ignore
        sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1") |> ignore
        
        // Add solution folders
        sb.AppendLine("Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"src\", \"src\", \"{827E0CD3-B72D-47B6-A68D-7590B98EB39B}\"") |> ignore
        sb.AppendLine("EndProject") |> ignore
        sb.AppendLine("Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"tests\", \"tests\", \"{0AB3BF05-4346-4AA6-1389-037BE0695223}\"") |> ignore
        sb.AppendLine("EndProject") |> ignore
        
        // Add projects
        sb.AppendLine($"Project(\"{{F2A71F9B-5D33-465A-A702-920D77279786}}\") = \"{options.LibraryName}\", \"src\\{options.LibraryName}\\{options.LibraryName}.fsproj\", \"{libraryProjectGuid}\"") |> ignore
        sb.AppendLine("EndProject") |> ignore
        sb.AppendLine($"Project(\"{{F2A71F9B-5D33-465A-A702-920D77279786}}\") = \"{options.LibraryName}.Tests\", \"tests\\{options.LibraryName}.Tests\\{options.LibraryName}.Tests.fsproj\", \"{testsProjectGuid}\"") |> ignore
        sb.AppendLine("EndProject") |> ignore
        
        // Add solution configurations
        sb.AppendLine("Global") |> ignore
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution") |> ignore
        sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU") |> ignore
        sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU") |> ignore
        sb.AppendLine("\tEndGlobalSection") |> ignore
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution") |> ignore
        sb.AppendLine($"\t\t{libraryProjectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU") |> ignore
        sb.AppendLine($"\t\t{libraryProjectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU") |> ignore
        sb.AppendLine($"\t\t{libraryProjectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU") |> ignore
        sb.AppendLine($"\t\t{libraryProjectGuid}.Release|Any CPU.Build.0 = Release|Any CPU") |> ignore
        sb.AppendLine($"\t\t{testsProjectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU") |> ignore
        sb.AppendLine($"\t\t{testsProjectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU") |> ignore
        sb.AppendLine($"\t\t{testsProjectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU") |> ignore
        sb.AppendLine($"\t\t{testsProjectGuid}.Release|Any CPU.Build.0 = Release|Any CPU") |> ignore
        sb.AppendLine("\tEndGlobalSection") |> ignore
        sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution") |> ignore
        sb.AppendLine("\t\tHideSolutionNode = FALSE") |> ignore
        sb.AppendLine("\tEndGlobalSection") |> ignore
        sb.AppendLine("\tGlobalSection(NestedProjects) = preSolution") |> ignore
        sb.AppendLine($"\t\t{libraryProjectGuid} = {{827E0CD3-B72D-47B6-A68D-7590B98EB39B}}") |> ignore
        sb.AppendLine($"\t\t{testsProjectGuid} = {{0AB3BF05-4346-4AA6-1389-037BE0695223}}") |> ignore
        sb.AppendLine("\tEndGlobalSection") |> ignore
        sb.AppendLine("EndGlobal") |> ignore
        
        File.WriteAllText(solutionPath, sb.ToString())

    /// Generate the main library project file
    let generateLibraryProjectFile (options: ProjectOptions) (projectPath: string) (codeFiles: CodeGenerator.CodeSection list) =
        let sb = StringBuilder()

        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("  <PropertyGroup>") |> ignore
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>") |> ignore
        sb.AppendLine("    <GenerateDocumentationFile>true</GenerateDocumentationFile>") |> ignore
        sb.AppendLine($"    <RootNamespace>{options.Namespace}</RootNamespace>") |> ignore
        sb.AppendLine($"    <AssemblyName>{options.LibraryName}</AssemblyName>") |> ignore
        sb.AppendLine("  </PropertyGroup>") |> ignore
        sb.AppendLine() |> ignore

        // Add F# source files in correct order
        sb.AppendLine("  <ItemGroup>") |> ignore
        
        // Order files by the Order property
        let orderedFiles = codeFiles |> List.sortBy (fun file -> file.Order)
        
        for file in orderedFiles do
            sb.AppendLine($"    <Compile Include=\"{file.FileName}\" />") |> ignore
            
        sb.AppendLine("  </ItemGroup>") |> ignore
        sb.AppendLine() |> ignore

        // Add NuGet package references
        if not options.NuGetPackages.IsEmpty then
            sb.AppendLine("  <ItemGroup>") |> ignore
            for (package, version) in options.NuGetPackages do
                sb.AppendLine($"    <PackageReference Include=\"{package}\" Version=\"{version}\" />") |> ignore
            sb.AppendLine("  </ItemGroup>") |> ignore
            sb.AppendLine() |> ignore

        // Add project references
        if not options.References.IsEmpty then
            sb.AppendLine("  <ItemGroup>") |> ignore
            for reference in options.References do
                sb.AppendLine($"    <ProjectReference Include=\"{reference}\" />") |> ignore
            sb.AppendLine("  </ItemGroup>") |> ignore
            sb.AppendLine() |> ignore

        sb.AppendLine("</Project>") |> ignore

        // Write the project file
        File.WriteAllText(projectPath, sb.ToString())

    /// Generate a test project file
    let generateTestProjectFile (options: ProjectOptions) (testProjectPath: string) =
        let sb = StringBuilder()

        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("  <PropertyGroup>") |> ignore
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>") |> ignore
        sb.AppendLine("    <GenerateDocumentationFile>true</GenerateDocumentationFile>") |> ignore
        sb.AppendLine("    <IsPackable>false</IsPackable>") |> ignore
        sb.AppendLine("  </PropertyGroup>") |> ignore
        sb.AppendLine() |> ignore

        // Add F# source files
        sb.AppendLine("  <ItemGroup>") |> ignore
        sb.AppendLine("    <Compile Include=\"LibraryTests.fs\" />") |> ignore
        sb.AppendLine("    <Compile Include=\"Program.fs\" />") |> ignore
        sb.AppendLine("  </ItemGroup>") |> ignore
        sb.AppendLine() |> ignore

        // Add package references
        sb.AppendLine("  <ItemGroup>") |> ignore
        sb.AppendLine("    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.9.0\" />") |> ignore
        sb.AppendLine("    <PackageReference Include=\"xunit\" Version=\"2.7.0\" />") |> ignore
        sb.AppendLine("    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"2.5.7\" />") |> ignore
        sb.AppendLine("  </ItemGroup>") |> ignore
        sb.AppendLine() |> ignore

        // Add project reference to the main library
        sb.AppendLine("  <ItemGroup>") |> ignore
        sb.AppendLine($"    <ProjectReference Include=\"..\\..\\src\\{options.LibraryName}\\{options.LibraryName}.fsproj\" />") |> ignore
        sb.AppendLine("  </ItemGroup>") |> ignore
        sb.AppendLine() |> ignore

        sb.AppendLine("</Project>") |> ignore

        // Write the project file
        File.WriteAllText(testProjectPath, sb.ToString())

    /// Generate test file with sample usage
    let generateTestFile (options: ProjectOptions) (testFilePath: string) =
        let sb = StringBuilder()

        sb.AppendLine("module LibraryTests") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("open System") |> ignore
        sb.AppendLine("open Xunit") |> ignore
        sb.AppendLine($"open {options.Namespace}") |> ignore
        sb.AppendLine($"open {options.Namespace}.Wrappers") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("[<Fact>]") |> ignore
        sb.AppendLine("let ``Library should load correctly``() =") |> ignore
        sb.AppendLine("    // This test verifies that the native library can be loaded") |> ignore
        sb.AppendLine("    // Replace with actual function calls from your library") |> ignore
        sb.AppendLine("    try") |> ignore
        sb.AppendLine("        // Example: let result = YourFunction(10)") |> ignore
        sb.AppendLine("        // Assert.Equal(expected, result)") |> ignore
        sb.AppendLine("        Assert.True(true, \"Library loaded successfully\")") |> ignore
        sb.AppendLine("    with ex ->") |> ignore
        sb.AppendLine("        Assert.True(false, $\"Failed to load library: {ex.Message}\")") |> ignore

        // Write the test file
        File.WriteAllText(testFilePath, sb.ToString())

    /// Generate test program file
    let generateTestProgramFile (testProgramPath: string) =
        let content = 
            """module Program

// Entry point for the xUnit test runner
[<EntryPoint>]
let main _ = 0
"""
        File.WriteAllText(testProgramPath, content)

    /// Generate the README file
    let generateReadmeFile (options: ProjectOptions) (readmeFile: string) =
        let sb = StringBuilder()

        sb.AppendLine($"# {options.LibraryName} F# Bindings") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("Generated by Farscape: F# Native Library Binding Generator") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("## Overview") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine($"This package provides F# bindings for the native {options.LibraryName} library. It was automatically generated based on the C++ header file at:") |> ignore
        sb.AppendLine($"`{options.HeaderFile}`") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("## Project Structure") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("The generated binding library follows a layered architecture:") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("- **Types.fs** - Contains all type definitions (structs, enums, etc.)") |> ignore
        sb.AppendLine("- **Bindings.fs** - Raw P/Invoke declarations for native functions") |> ignore
        sb.AppendLine("- **Wrappers.fs** - Idiomatic F# wrappers around the raw bindings") |> ignore
        sb.AppendLine("- **Memory.fs** - Memory management utilities") |> ignore
        sb.AppendLine("- **Delegates.fs** - Function pointer and callback handling") |> ignore
        sb.AppendLine("- **Extensions.fs** - Extension methods and additional utilities") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("## Usage") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("```fsharp") |> ignore
        sb.AppendLine($"open {options.Namespace}") |> ignore
        sb.AppendLine($"open {options.Namespace}.Wrappers") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("// Example usage") |> ignore
        sb.AppendLine("let result = SomeFunction(arg1, arg2)") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("// Or with wrapper classes") |> ignore
        sb.AppendLine("use context = SomeContextClass.Create()") |> ignore
        sb.AppendLine("let value = context.SomeMethod()") |> ignore
        sb.AppendLine("```") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("## Installation") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("1. Add a reference to this project in your F# solution") |> ignore
        sb.AppendLine("2. Make sure the native library is available in your application's path") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("## Requirements") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("- .NET 9.0 or higher") |> ignore
        sb.AppendLine($"- Native {options.LibraryName} library") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("## License") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("MIT") |> ignore

        // Write the README file
        File.WriteAllText(readmeFile, sb.ToString())
        
    /// Save the generated code to files
    let saveGeneratedCode (outputDir: string) (code: CodeGenerator.GeneratedCode) =
        for section in code.Sections do
            let filePath = Path.Combine(outputDir, section.FileName)
            File.WriteAllText(filePath, section.Content)
            
    /// Generate all project files and directory structure
    let generateProject (options: ProjectOptions) (generatedCode: CodeGenerator.GeneratedCode) =
        // Create the main directory structure
        let baseDir = options.OutputDirectory
        let srcDir = Path.Combine(baseDir, "src")
        let testsDir = Path.Combine(baseDir, "tests")
        let projectDir = Path.Combine(srcDir, options.LibraryName)
        let testProjectDir = Path.Combine(testsDir, $"{options.LibraryName}.Tests")
        
        ensureDirectory baseDir
        ensureDirectory srcDir
        ensureDirectory testsDir
        ensureDirectory projectDir
        ensureDirectory testProjectDir
        
        // Generate solution file
        let solutionPath = Path.Combine(baseDir, $"{options.LibraryName}.sln")
        generateSolutionFile options solutionPath
        
        // Generate and save code files
        saveGeneratedCode projectDir generatedCode
        
        // Generate project files
        let projectPath = Path.Combine(projectDir, $"{options.LibraryName}.fsproj")
        generateLibraryProjectFile options projectPath generatedCode.Sections
        
        // Generate test project
        let testProjectPath = Path.Combine(testProjectDir, $"{options.LibraryName}.Tests.fsproj")
        generateTestProjectFile options testProjectPath
        
        // Generate test files
        let testFilePath = Path.Combine(testProjectDir, "LibraryTests.fs")
        generateTestFile options testFilePath
        
        let testProgramPath = Path.Combine(testProjectDir, "Program.fs")
        generateTestProgramFile testProgramPath
        
        // Generate README file
        let readmePath = Path.Combine(baseDir, "README.md")
        generateReadmeFile options readmePath
        
        // Return paths to generated files
        (solutionPath, projectPath, testProjectPath)