using UnityEngine;

namespace PathTracing
{
    public static class ComputeDispatch
    {
        // Keep this call centralized so a debugger breakpoint can observe every project dispatch.
        public static void Dispatch(ComputeShader shader, int kernel, int groupsX, int groupsY, int groupsZ)
        {
            shader.Dispatch(kernel, groupsX, groupsY, groupsZ);
        }
    }
}
