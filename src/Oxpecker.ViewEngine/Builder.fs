namespace Oxpecker.ViewEngine

open System.Runtime.CompilerServices
open System.Text
open System.Threading
open Tools

[<AutoOpen>]
module Builder =

    [<Struct>]
    type HtmlAttribute = { Name: string; Value: string | null }

    type HtmlElement =
        abstract member Render: StringBuilder -> unit

    type HtmlTag =
        inherit HtmlElement
        abstract member AddAttribute: HtmlAttribute -> unit

    type HtmlContainer =
        inherit HtmlElement
        abstract member AddChild: HtmlElement -> unit

    /// Text node that will be HTML-escaped
    type RegularTextNode(text: string | null) =
        member this.Render(sb: StringBuilder) = sb |> CustomWebUtility.htmlEncode text
        interface HtmlElement with
            member this.Render sb = this.Render sb

    /// Attributes of a tag: the first attribute is stored inline, so the common single-attribute case needs no queue item
    [<Struct>]
    type internal AttributeList =
        val mutable First: HtmlAttribute
        val mutable Rest: CustomQueue<HtmlAttribute>
        member this.Add(attribute: HtmlAttribute) =
            // an attribute with a null name goes to the queue, so the order and contents stay as they were
            if
                isNull(box this.First.Name)
                && this.Rest.IsEmpty
                && isNotNull(box attribute.Name)
            then
                this.First <- attribute
            else
                this.Rest.Enqueue(attribute)
        member this.AsEnumerable() =
            let first = this.First
            let rest = this.Rest
            seq {
                if isNotNull(box first.Name) then
                    yield first
                yield! rest.AsEnumerable()
            }

    module internal RenderHelpers =
        let inline renderAttribute (sb: StringBuilder) (attr: HtmlAttribute) =
            if isNull attr.Value then
                sb.Append(' ').Append(attr.Name) |> ignore
            else
                sb.Append(' ').Append(attr.Name).Append("=\"") |> ignore
                sb |> CustomWebUtility.htmlEncode attr.Value
                sb.Append('"') |> ignore

        let inline renderStartTag (tagName: string) (sb: StringBuilder) (attributes: AttributeList) =
            sb.Append('<').Append(tagName) |> ignore
            if isNotNull(box attributes.First.Name) then
                renderAttribute sb attributes.First
            attributes.Rest |> CustomQueue.iter(fun attr -> renderAttribute sb attr)
            sb.Append('>') |> ignore

        let inline renderEndTag (tagName: string) (sb: StringBuilder) =
            sb.Append("</").Append(tagName).Append('>') |> ignore

    /// One child after the first, when it can't link through its own NextSibling field
    [<Sealed>]
    type internal ChildItem(value: HtmlElement) =
        member this.Value = value
        [<DefaultValue(false)>]
        val mutable Next: obj | null

    /// Children of a container: the first child is stored inline, so the common single-child case needs no queue item.
    /// A text first child is stored as the string itself (no RegularTextNode), so First is an HtmlElement or a string.
    /// The other children form a circular singly linked list of which only the tail is stored (tail's next is the head).
    /// A RegularNode is linked through its own NextSibling field the first time it follows a first child anywhere
    /// (claimed atomically, so a node shared between threads or added twice is never linked twice); any other element,
    /// or a RegularNode already linked elsewhere, is linked through a ChildItem.
    [<Struct>]
    type internal ChildList =
        val mutable First: obj | null
        val mutable Tail: obj | null

        static member private NextOf(link: obj | null) : obj | null =
            match link with
            | :? ChildItem as item -> item.Next
            | :? RegularNode as node -> node.NextSibling
            | _ -> null

        static member private SetNext(link: obj | null, next: obj | null) =
            match link with
            | :? ChildItem as item -> item.Next <- next
            | :? RegularNode as node -> node.NextSibling <- next
            | _ -> ()

        static member private ValueOf(link: obj | null) : HtmlElement =
            match link with
            | :? ChildItem as item -> item.Value
            | :? RegularNode as node -> node
            | _ -> Unchecked.defaultof<_>

        member this.Enqueue(element: HtmlElement) =
            let tail = this.Tail
            let linkedNode =
                match box element with
                | :? RegularNode as node ->
                    let head = if isNull tail then box node else ChildList.NextOf tail
                    isNull(Interlocked.CompareExchange(&node.NextSibling, head, null))
                | _ -> false
            let link: obj | null =
                if linkedNode then
                    box element
                else
                    let item = ChildItem(element)
                    item.Next <- (if isNull tail then box item else ChildList.NextOf tail)
                    item
            if isNotNull tail then
                ChildList.SetNext(tail, link)
            this.Tail <- link

        member this.Add(element: HtmlElement) =
            // a null element goes to the list, so the order and contents stay as they were
            if isNull this.First && isNull this.Tail && isNotNull(box element) then
                this.First <- element
            else
                this.Enqueue(element)
        member this.AddText(text: string | null) =
            // a plain null check: the NonNull active pattern would allocate a Choice per call
            if isNotNull text && isNull this.First && isNull this.Tail then
                this.First <- text
            else
                this.Enqueue(RegularTextNode text)
        member this.Render(sb: StringBuilder) =
            match this.First with
            | null -> ()
            | :? string as text -> sb |> CustomWebUtility.htmlEncode text
            | first -> (first :?> HtmlElement).Render(sb)
            let tail = this.Tail
            if isNotNull tail then
                let mutable link = tail
                let mutable go = true
                while go do
                    link <- ChildList.NextOf link
                    (ChildList.ValueOf link).Render(sb)
                    go <- not(obj.ReferenceEquals(link, tail))
        member this.AsEnumerable() =
            let first = this.First
            let tail = this.Tail
            seq {
                match first with
                | null -> ()
                | :? string as text -> yield RegularTextNode text :> HtmlElement
                | element -> yield element :?> HtmlElement
                if isNotNull tail then
                    let mutable link = tail
                    let mutable go = true
                    while go do
                        link <- ChildList.NextOf link
                        yield ChildList.ValueOf link
                        go <- not(obj.ReferenceEquals(link, tail))
            }

    /// Node with both children and attributes
    and RegularNode(tagName: string) =
        let mutable children: ChildList = Unchecked.defaultof<_>
        let mutable attributes: AttributeList = Unchecked.defaultof<_>
        /// Next child of the parent this node was linked into (see ChildList)
        [<DefaultValue(false)>]
        val mutable internal NextSibling: obj | null
        member this.Children = children.AsEnumerable()
        member this.Attributes = attributes.AsEnumerable()
        member this.TagName = tagName
        member this.Render(sb: StringBuilder) =
            RenderHelpers.renderStartTag tagName sb attributes
            children.Render(sb)
            RenderHelpers.renderEndTag tagName sb
        member this.AddAttribute(attribute: HtmlAttribute) = attributes.Add(attribute)
        member this.AddChild(element: HtmlElement) = children.Add(element)
        /// Adds an HTML-escaped text child, the same as AddChild(RegularTextNode text)
        member this.AddText(text: string | null) = children.AddText(text)
        interface HtmlElement with
            member this.Render sb = this.Render sb
        interface HtmlTag with
            member this.AddAttribute attribute = this.AddAttribute attribute
        interface HtmlContainer with
            member this.AddChild element = this.AddChild element

    /// Node with children only (no attributes)
    type FragmentNode() =
        let mutable children: ChildList = Unchecked.defaultof<_>
        member this.Children = children.AsEnumerable()
        member this.AddChild(element: HtmlElement) = children.Add(element)
        member this.Render(sb: StringBuilder) = children.Render(sb)
        interface HtmlContainer with
            member this.Render sb = this.Render sb
            member this.AddChild element = this.AddChild element

    /// Node with attributes only (no children)
    type VoidNode(tagName: string) =
        let mutable attributes: AttributeList = Unchecked.defaultof<_>
        member this.Attributes = attributes.AsEnumerable()
        member this.Render(sb: StringBuilder) =
            RenderHelpers.renderStartTag tagName sb attributes
        member this.AddAttribute(attribute: HtmlAttribute) = attributes.Add(attribute)
        member this.TagName = tagName
        interface HtmlTag with
            member this.Render sb = this.Render sb
            member this.AddAttribute attribute = this.AddAttribute attribute

    /// Text node that will NOT be HTML-escaped
    type RawTextNode(text: string | null) =
        member this.Render(sb: StringBuilder) = text |> sb.Append |> ignore
        interface HtmlElement with
            member this.Render sb = this.Render sb

    /// Integer node that will NOT be HTML-escaped
    type IntNode(value: int) =
        member this.Render(sb: StringBuilder) = value |> sb.Append |> ignore
        interface HtmlElement with
            member this.Render sb = this.Render sb

    /// Node with a prerendered prefix and suffix around its children
    type PrerenderedNode(prefix: string, suffix: string) =
        let mutable children: ChildList = Unchecked.defaultof<_>
        member this.Children = children.AsEnumerable()
        member this.AddChild(element: HtmlElement) = children.Add(element)
        member this.Render(sb: StringBuilder) =
            sb.Append(prefix) |> ignore
            children.Render(sb)
            sb.Append(suffix) |> ignore
        interface HtmlContainer with
            member this.Render sb = this.Render sb
            member this.AddChild element = this.AddChild element

    /// Placeholder that records where the dynamic part of a template begins
    type internal HoleMarker() =
        let mutable buffer: StringBuilder | null = null
        let mutable position = -1
        let mutable count = 0
        member this.Position = position
        /// Forgets renders that happened while the template was being built
        member this.Reset() =
            buffer <- null
            position <- -1
            count <- 0
        /// True only when the hole was rendered exactly once, into this very buffer
        member this.WasRenderedOnceInto(sb: StringBuilder) =
            count = 1 && obj.ReferenceEquals(buffer, sb)
        member this.Render(sb: StringBuilder) =
            buffer <- sb
            position <- sb.Length
            count <- count + 1
        interface HtmlElement with
            member this.Render sb = this.Render sb

    /// Create text node that will NOT be HTML-escaped
    let inline raw text = RawTextNode text

    /// <summary>
    /// Renders an element together with all its children into a static HTML snapshot.
    /// Use it to render static parts of a view once, instead of re-rendering them on every request.
    /// </summary>
    /// <remarks>
    /// The snapshot is taken eagerly, at the moment of the call. Children or attributes added
    /// to the original element afterwards will not be reflected in the returned node.
    /// </remarks>
    let prerender (view: #HtmlElement) =
        let sb = StringBuilderPool.Get()
        try
            view.Render sb
            RawTextNode(sb.ToString())
        finally
            StringBuilderPool.Return(sb)

    /// <summary>
    /// Renders the static part of a template with a hole in it once, and returns a factory
    /// creating nodes that fill the hole. Use it for layouts, where only a small part of the
    /// markup changes between renders.
    /// </summary>
    /// <param name="template">
    /// Function that receives the hole and places it inside the markup. It has to use the hole exactly once.
    /// </param>
    /// <remarks>
    /// The static part is rendered eagerly, at the moment of the call, exactly like <c>prerender</c> does.
    /// </remarks>
    let prerenderAround (template: HtmlElement -> #HtmlElement) =
        let hole = HoleMarker()
        let view = template hole
        let sb = StringBuilderPool.Get()
        let prefix, suffix =
            try
                // the template may have rendered the hole into a buffer of its own while being built
                hole.Reset()
                view.Render sb
                if not(hole.WasRenderedOnceInto sb) then
                    invalidArg (nameof template) "Template has to render the provided hole exactly once"
                sb.ToString(0, hole.Position), sb.ToString(hole.Position, sb.Length - hole.Position)
            finally
                StringBuilderPool.Return(sb)
        fun () -> PrerenderedNode(prefix, suffix)

    type HtmlContainerFun = HtmlContainer -> unit

    // builder methods
    type HtmlContainer with
        member inline _.Combine
            ([<InlineIfLambda>] first: HtmlContainerFun, [<InlineIfLambda>] second: HtmlContainerFun)
            : HtmlContainerFun =
            fun builder ->
                first builder
                second builder

        member inline _.Zero() : HtmlContainerFun = ignore

        member inline _.Delay([<InlineIfLambda>] delay: unit -> HtmlContainerFun) : HtmlContainerFun = delay()

        member inline _.For(values: #seq<'T>, [<InlineIfLambda>] body: 'T -> HtmlContainerFun) : HtmlContainerFun =
            fun builder ->
                for value in values do
                    let f = body value
                    f builder

        member inline _.Yield(element: #HtmlElement) : HtmlContainerFun = _.AddChild(element)

        member inline _.YieldFrom(elements: #seq<#HtmlElement>) : HtmlContainerFun =
            fun builder ->
                for element in elements do
                    builder.AddChild(element)

        member inline _.Yield(text: string | null) : HtmlContainerFun =
            fun builder ->
                match builder with
                | :? RegularNode as node -> node.AddText text
                | _ -> builder.AddChild(RegularTextNode text)

        member inline _.Yield(value: int) : HtmlContainerFun = _.AddChild(IntNode value)

    type HtmlContainerExtensions =
        [<Extension>]
        static member inline Run(this: #HtmlContainer, [<InlineIfLambda>] runExpr: HtmlContainerFun) =
            runExpr this
            this
