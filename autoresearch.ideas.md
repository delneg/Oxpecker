# Ideas backlog (not done)
- ViewEngine builder (dynamic views, ~1.7 KB for a small page): one CustomQueueItem (32 B) per child and per
  attribute, plus a closure per `for` iteration in the CE. Options: inline first child/attribute slot in the
  node, or array-backed children. Needs care: elements can be shared between parents, so no intrusive links.
- subRoutef: per request the factory rebuilds endpoints; applyHttpVerbsToEndpoints uses lazy Seq.map, so each
  enumeration re-creates SimpleEndpoint and seq machinery. Making it eager changes when "verbs intersect"
  errors fire, so it's a behaviour change to discuss with the maintainer first.
- bindForm/bindJson handlers: skip the outer task state machine when the bind task completed (needs the same
  exception-parity care as exp 11; ~30 ns on >1 us requests).
- SystemTextJsonSerializer.Serialize: sync fast path without the task builder (CPU only, parity care).
- validatePreconditions: ETag/Last-Modified strings are formatted per request; could be precomputed in the
  validatePreconditions middleware (values fixed per endpoint) but ValidatePreconditions is public.
- routef: boxed int per numeric segment (24 B) is inherent to MethodInvoker; a typed invoker per handler
  type (MakeGenericType at startup) could avoid MethodInvoker overhead (~10-20 ns) but adds complexity.
- validateModelWith: ResizeArray allocated even when valid (32 B); DataAnnotations Validator dominates anyway.
- Streaming range responses: F# interpolation with %i uses printf machinery; only for range requests.
