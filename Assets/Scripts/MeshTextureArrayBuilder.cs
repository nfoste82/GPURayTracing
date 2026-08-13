using System.Collections.Generic;
using UnityEngine;

public static class MeshTextureArrayBuilder
{
    public static Texture2DArray Build(List<Texture2D> textures, Texture2D fallback, string arrayName, Color fallbackColor, bool linear)
    {
        int textureCount = Mathf.Max(1, textures.Count);
        int width = 1;
        int height = 1;
        for (int i = 0; i < textures.Count; i++)
        {
            if (textures[i] != null)
            {
                width = Mathf.Max(width, textures[i].width);
                height = Mathf.Max(height, textures[i].height);
            }
        }

        var result = new Texture2DArray(width, height, textureCount, TextureFormat.RGBA32, false, linear)
        {
            name = arrayName,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };
        for (int i = 0; i < textureCount; i++)
        {
            CopyToSlice(i < textures.Count ? textures[i] : fallback, result, i, fallbackColor);
        }

        result.Apply(false, false);
        return result;
    }

    private static void CopyToSlice(Texture2D source, Texture2DArray destination, int slice, Color fallbackColor)
    {
        int width = destination.width;
        int height = destination.height;
        var pixels = new Color32[width * height];
        if (source == null)
        {
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fallbackColor;
        }
        else if (source.isReadable && source.width == width && source.height == height)
        {
            pixels = source.GetPixels32();
        }
        else if (source.isReadable)
        {
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = source.GetPixelBilinear((x + 0.5f) / width, (y + 0.5f) / height);
        }
        else
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                destination.isDataSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var readable = new Texture2D(width, height, TextureFormat.RGBA32, false, !destination.isDataSRGB);
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readable.Apply(false, false);
                pixels = readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (Application.isPlaying)
                {
                    Object.Destroy(readable);
                }
                else
                {
                    Object.DestroyImmediate(readable);
                }
            }
        }

        destination.SetPixels32(pixels, slice);
    }
}
