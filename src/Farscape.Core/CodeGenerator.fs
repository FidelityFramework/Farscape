namespace Farscape.Core
        
open System
open System.Text

module CodeGenerator =
    type CodeSection = {
        FileName: string
        Content: string
        Order: int
    }
    
    type GeneratedCode = {
        Sections: CodeSection list
    }

    type BindingGeneratorOptions = {
        LibraryName: string
        Namespace: string
        IncludePaths: string list
        OutputDirectory: string
        Verbose: bool
    }

    type AdvancedBindingGenerator(options: BindingGeneratorOptions) =
        member _.GeneratePInvokeDeclarations(declarations: CppParser.Declaration list) =
            let sb = StringBuilder()
            sb.AppendLine($"module {options.Namespace}.NativeBindings") |> ignore
            sb.AppendLine("open System.Runtime.InteropServices") |> ignore
            sb.AppendLine() |> ignore

            // Filter and process function declarations
            let functions =
                declarations
                |> List.choose (function
                    | CppParser.Declaration.Function f -> Some f
                    | _ -> None)

            for func in functions do
                // Generate XML documentation
                match func.Documentation with
                | Some doc ->
                    sb.AppendLine($"/// {doc}") |> ignore
                | None -> ()

                // Generate P/Invoke with enhanced attributes
                let returnType = TypeMapper.getFSharpType func.ReturnType
                let parameters =
                    func.Parameters
                    |> List.map (fun (name, paramType) ->
                        let fsharpType = TypeMapper.getFSharpType paramType
                        $"{name}: {fsharpType}")
                    |> String.concat ", "

                sb.AppendLine($"[<DllImport(\"{options.LibraryName}\", CallingConvention = CallingConvention.Cdecl)>]") |> ignore
                sb.AppendLine($"extern {returnType} {func.Name}({parameters})") |> ignore
                sb.AppendLine() |> ignore

            sb.ToString()

        member _.GenerateWrapperFunctions(declarations: CppParser.Declaration list) =
            let sb = StringBuilder()
            sb.AppendLine($"module {options.Namespace}.Wrappers") |> ignore
            sb.AppendLine("open System") |> ignore
            sb.AppendLine($"open {options.Namespace}.NativeBindings") |> ignore
            sb.AppendLine($"open {options.Namespace}.Types") |> ignore
            sb.AppendLine() |> ignore

            let functions =
                declarations
                |> List.choose (function
                    | CppParser.Declaration.Function f -> Some f
                    | _ -> None)

            for func in functions do
                let returnType = TypeMapper.getFSharpType func.ReturnType
                let parameters =
                    func.Parameters
                    |> List.map (fun (name, paramType) ->
                        let fsharpType = TypeMapper.getFSharpType paramType
                        $"{name}: {fsharpType}")
                    |> String.concat ", "

                // Advanced error handling for integer return types
                sb.AppendLine($"/// Wrapped version of {func.Name} with enhanced error handling") |> ignore
                sb.AppendLine($"let wrap{func.Name} ({parameters}) : {returnType} =") |> ignore

                let paramList = String.concat ", " (func.Parameters |> List.map fst)
                if returnType = "int" || returnType = "int32" then
                    sb.AppendLine($"    let result = NativeBindings.{func.Name}({paramList})") |> ignore
                    sb.AppendLine("    if result < 0 then") |> ignore
                    sb.AppendLine("        failwith (\"Error in " + func.Name + ": \" + string result)") |> ignore
                    sb.AppendLine("    result") |> ignore
                else
                    sb.AppendLine($"    NativeBindings.{func.Name}({paramList})") |> ignore

                sb.AppendLine() |> ignore

            sb.ToString()

        member _.GenerateStructWrappers(declarations: CppParser.Declaration list) =
            let sb = StringBuilder()
            sb.AppendLine($"module {options.Namespace}.StructWrappers") |> ignore
            sb.AppendLine("open System") |> ignore
            sb.AppendLine("open System.Runtime.InteropServices") |> ignore
            sb.AppendLine() |> ignore

            let structs =
                declarations
                |> List.choose (function
                    | CppParser.Declaration.Struct s -> Some s
                    | _ -> None)

            for struct' in structs do
                sb.AppendLine($"/// Wrapper for {struct'.Name} with memory management") |> ignore
                sb.AppendLine($"type {struct'.Name}Wrapper() =") |> ignore

                // Add fields
                for field in struct'.Fields do
                    let fsharpType = TypeMapper.getFSharpType field.Type
                    sb.AppendLine($"    member val {field.Name}: {fsharpType} = Unchecked.defaultof<{fsharpType}> with get, set") |> ignore

                sb.AppendLine() |> ignore

                // Add native memory conversion methods
                sb.AppendLine("    /// Convert to native memory") |> ignore
                sb.AppendLine("    member this.ToNativeMemory() =") |> ignore
                sb.AppendLine("        let size = Marshal.SizeOf(this)") |> ignore
                sb.AppendLine("        let ptr = Marshal.AllocHGlobal(size)") |> ignore
                sb.AppendLine("        Marshal.StructureToPtr(this, ptr, false)") |> ignore
                sb.AppendLine("        ptr") |> ignore
                sb.AppendLine() |> ignore

                sb.AppendLine("    /// Create from native memory") |> ignore
                sb.AppendLine($"    static member FromNativeMemory(ptr: nativeint) =") |> ignore
                sb.AppendLine($"        Marshal.PtrToStructure<{struct'.Name}Wrapper>(ptr)") |> ignore

                sb.AppendLine() |> ignore

            sb.ToString()

        member this.GenerateBindings(declarations: CppParser.Declaration list) =
            [
                {
                    FileName = "NativeBindings.fs"
                    Content = this.GeneratePInvokeDeclarations(declarations)
                    Order = 1
                }
                {
                    FileName = "Wrappers.fs"
                    Content = this.GenerateWrapperFunctions(declarations)
                    Order = 2
                }
                {
                    FileName = "StructWrappers.fs"
                    Content = this.GenerateStructWrappers(declarations)
                    Order = 3
                }
            ]

    /// Generate code from declarations
    let generateCode (declarations: CppParser.Declaration list) (namespace': string) (libraryName: string) =
        let options: BindingGeneratorOptions = {
            LibraryName = libraryName
            Namespace = namespace'
            IncludePaths = []
            OutputDirectory = ""
            Verbose = false
        }
        
        let generator = AdvancedBindingGenerator(options)
        let sections = generator.GenerateBindings(declarations)
        
        { Sections = sections }