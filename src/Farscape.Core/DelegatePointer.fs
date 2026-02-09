namespace Farscape.Core

open System
open System.Text
open System.Runtime.InteropServices

module DelegatePointer =
    type FunctionPointerSignature = {
        ReturnType: string
        ParameterTypes: string list
        CallingConvention: CallingConvention
    }
    
    type DelegateTypeDefinition = {
        Name: string
        Signature: FunctionPointerSignature
        Documentation: string option
    }
    
    let extractFunctionPointerSignature (cppType: string) : FunctionPointerSignature option =
        let pattern = @"([\w\s\*]+)\s*\(\*\)\s*\(([\w\s\*,]*)\)"
        let regex = System.Text.RegularExpressions.Regex(pattern)
        let match' = regex.Match(cppType)
        
        if match'.Success then
            let returnType = match'.Groups.[1].Value.Trim()
            let paramStr = match'.Groups.[2].Value.Trim()
            
            let paramTypes = 
                if String.IsNullOrWhiteSpace(paramStr) then
                    []
                else
                    paramStr.Split(',')
                    |> Array.map (fun s -> s.Trim())
                    |> Array.toList
                    
            Some {
                ReturnType = returnType
                ParameterTypes = paramTypes
                CallingConvention = CallingConvention.Cdecl
            }
        else
            None
    
    let generateDelegateType (signature: FunctionPointerSignature) (name: string) (documentation: string option) =
        let parameters = 
            signature.ParameterTypes
            |> List.mapi (fun i paramType -> 
                let fsharpType = TypeMapper.getFSharpType paramType
                $"arg{i}: {fsharpType}")
            |> String.concat ", "
            
        let returnType = TypeMapper.getFSharpType signature.ReturnType
        
        let docComment = 
            match documentation with
            | None -> ""
            | Some text ->
                let lines = text.Split([|'\n'|], StringSplitOptions.RemoveEmptyEntries)
                let sb = StringBuilder()
                sb.AppendLine("/// <summary>") |> ignore
                for line in lines do
                    sb.AppendLine($"/// {line.Trim()}") |> ignore
                sb.AppendLine("/// </summary>") |> ignore
                sb.ToString()
        
        let callingConvention = signature.CallingConvention.ToString()
        
        $"{docComment}[<UnmanagedFunctionPointer(CallingConvention.{callingConvention})>]\ntype {name} = delegate of {parameters} -> {returnType}"
    
    let generateDelegateWrapper (delegateType: string) =
        $"let wrap{delegateType} (func: {delegateType}) : nativeint =\n    Marshal.GetFunctionPointerForDelegate(func) |> nativeint"
    
    let generateDelegateUnwrapper (delegateType: string) =
        $"let unwrap{delegateType} (ptr: nativeint) : {delegateType} =\n    Marshal.GetDelegateForFunctionPointer(ptr, typeof<{delegateType}>) :?> {delegateType}"
    
    let identifyFunctionPointers (declarations: CppParser.Declaration list) : DelegateTypeDefinition list =
        let rec processDeclarations (decls: CppParser.Declaration list) =
            let mutable delegates = []
    
            // Helper to process parameters
            let processParameters (parameters: (string * string) list) =
                for (name, paramType) in parameters do
                    match extractFunctionPointerSignature paramType with
                    | Some signature ->
                        let delegateName = $"{name}Delegate"
                        delegates <- { Name = delegateName; Signature = signature; Documentation = None } :: delegates
                    | None -> ()
    
            for decl in decls do
                match decl with
                | CppParser.Declaration.Function f ->
                    processParameters f.Parameters
    
                    // Check if return type is a function pointer
                    match extractFunctionPointerSignature f.ReturnType with
                    | Some signature ->
                        let delegateName = $"{f.Name}ReturnDelegate"
                        delegates <- { Name = delegateName; Signature = signature; Documentation = None } :: delegates
                    | None -> ()
    
                | CppParser.Declaration.Struct s ->
                    for field in s.Fields do
                        match extractFunctionPointerSignature field.Type with
                        | Some signature ->
                            let delegateName = $"{s.Name}_{field.Name}Delegate"
                            delegates <- { Name = delegateName; Signature = signature; Documentation = None } :: delegates
                        | None -> ()
    
                | CppParser.Declaration.Class c ->
                    processDeclarations c.Methods |> ignore

                    for field in c.Fields do
                        match extractFunctionPointerSignature field.Type with
                        | Some signature ->
                            let delegateName = $"{c.Name}_{field.Name}Delegate"
                            delegates <- { Name = delegateName; Signature = signature; Documentation = None } :: delegates
                        | None -> ()
    
                | CppParser.Declaration.Namespace ns ->
                    delegates <- processDeclarations ns.Declarations @ delegates
    
                | _ -> ()
    
            List.distinctBy (fun d -> d.Name) delegates
    
        processDeclarations declarations
        
    let generateDelegateTypes (functionPointers: DelegateTypeDefinition list) =
        functionPointers
        |> List.map (fun fp -> 
            generateDelegateType fp.Signature fp.Name fp.Documentation)
        |> String.concat "\n\n"
    
    let generateDelegateWrappers (functionPointers: DelegateTypeDefinition list) =
        functionPointers
        |> List.map (fun fp -> generateDelegateWrapper fp.Name)
        |> String.concat "\n\n"
    
    let generateDelegateUnwrappers (functionPointers: DelegateTypeDefinition list) =
        functionPointers
        |> List.map (fun fp -> generateDelegateUnwrapper fp.Name)
        |> String.concat "\n\n"