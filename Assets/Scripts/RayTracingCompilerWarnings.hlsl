// Unity 6.3's Metal translator reports warning 4000 transitively through deeply inlined
// traversal code even when structures, arrays, and out parameters are initialized. Unity's
// own ray-tracing shaders suppress the same false positive. Other warnings remain enabled.
#pragma warning(disable : 4000)
