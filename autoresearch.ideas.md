# Ideas backlog
- WriteText: avoid Encoding.UTF8.GetBytes array (encode into Response.BodyWriter / pooled buffer).
- routef: use ctx.Request.RouteValues instead of ctx.GetRouteData() (GetRouteData allocates a RouteData).
- routef without ctx: fixed-arity MethodInvoker.Invoke instead of Span over a new array.
- JSON: avoid RecyclableMemoryStream object + task state machine; serialize into pooled buffer writer.
- htmlView: render to pooled StringBuilder / encode directly; check Render.toHtmlDocStreamAsync.
- TryGetHeaderValue/TryGetQueryValue: Option allocation is API-mandated; check StringValues.ToString.
- Compose (>=>): closure and task builder cost.
- HttpMethods.IsHead instead of string <> compare.
