// Prints every public type and member signature of Oxpecker and Oxpecker.ViewEngine, sorted, for before/after diffs.
open System.Reflection
let flags = BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly
let ps (pars: ParameterInfo[]) = pars |> Array.map (fun p -> string p.ParameterType + " " + string p.Name) |> String.concat ", "
let lines = ResizeArray()
for asm in [ typeof<Oxpecker.SystemTextJsonSerializer>.Assembly; typeof<Oxpecker.ViewEngine.Tags.div>.Assembly ] do
    for t in asm.GetExportedTypes() do
        lines.Add(sprintf "T %s : %O [%O]" t.FullName t.BaseType t.Attributes)
        for i in t.GetInterfaces() do lines.Add(sprintf "I %s : %O" t.FullName i)
        for m in t.GetMembers(flags) do
            let sigStr =
                match m with
                | :? MethodInfo as mi ->
                    let gens = mi.GetGenericArguments() |> Array.map string |> String.concat ","
                    sprintf "%O %s<%s>(%s)" mi.ReturnType mi.Name gens (ps (mi.GetParameters()))
                | :? ConstructorInfo as ci -> sprintf ".ctor(%s)" (ps (ci.GetParameters()))
                | :? PropertyInfo as pi -> sprintf "P %O %s" pi.PropertyType pi.Name
                | :? FieldInfo as fi -> sprintf "F %O %s" fi.FieldType fi.Name
                | _ -> sprintf "%O %s" m.MemberType m.Name
            lines.Add(sprintf "M %s :: %s" t.FullName sigStr)
for l in Seq.sort lines do printfn "%s" l
