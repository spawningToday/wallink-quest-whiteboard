using UnityEngine;

namespace WallInk
{
    /// <summary>
    /// Shaders, materials and shared meshes. Shaders live under Resources so they are always
    /// included in the build and need no inspector wiring.
    /// </summary>
    public static class InkAssets
    {
        public const int SortBoard = 0;
        public const int SortBoardOverlay = 5;
        public const int SortInk = 10;
        public const int SortAirInk = 2000;
        public const int SortUiPanel = 3000;
        public const int SortUiButton = 3001;
        public const int SortUiText = 3002;
        public const int SortCursor = 4000;

        static Shader s_ink, s_board, s_text, s_glass;
        static Material s_inkMat, s_airInkMat, s_textMat, s_glassMat;
        static Mesh s_quad, s_disc;
        static Font s_font;

        public static Shader InkShader => s_ink != null ? s_ink : (s_ink = Load("WallInk/Ink"));
        public static Shader BoardShader => s_board != null ? s_board : (s_board = Load("WallInk/Board"));
        public static Shader GlassShader => s_glass != null ? s_glass : (s_glass = Load("WallInk/Glass"));
        public static Shader TextShader => s_text != null ? s_text : (s_text = Load("WallInk/Text"));

        static Shader Load(string path)
        {
            Shader s = Resources.Load<Shader>(path);
            if (s == null) s = Shader.Find(path);
            if (s == null) Debug.LogError("WallInk: shader '" + path + "' not found. Is Assets/WallInk/Resources present?");
            return s;
        }

        /// <summary>Shared material for board ink and flat UI (no depth write, painter's order).</summary>
        public static Material InkMaterial
        {
            get
            {
                if (s_inkMat == null) s_inkMat = new Material(InkShader) { name = "WallInk Ink" };
                return s_inkMat;
            }
        }

        /// <summary>Shared material for glass-style UI surfaces; per-renderer look comes from <see cref="SetGlass"/>.</summary>
        public static Material GlassMaterial
        {
            get
            {
                if (s_glassMat == null) s_glassMat = new Material(GlassShader) { name = "WallInk Glass" };
                return s_glassMat;
            }
        }

        static readonly int SizeId = Shader.PropertyToID("_Size");

        /// <summary>Sets a glass surface's tint, physical size, corner radius and rim width (metres).</summary>
        public static void SetGlass(Renderer r, Color tint, Vector2 size, float radius, float rimWidth = 0.0012f)
        {
            if (s_block == null) s_block = new MaterialPropertyBlock();
            r.GetPropertyBlock(s_block);
            s_block.SetColor(ColorId, tint);
            s_block.SetVector(SizeId, new Vector4(size.x, size.y, rimWidth, radius));
            r.SetPropertyBlock(s_block);
        }

        /// <summary>Ink that writes depth, for 3D air strokes that must occlude each other correctly.</summary>
        public static Material AirInkMaterial
        {
            get
            {
                if (s_airInkMat == null)
                {
                    s_airInkMat = new Material(InkShader) { name = "WallInk Air Ink" };
                    s_airInkMat.SetFloat("_ZWrite", 1f);
                }
                return s_airInkMat;
            }
        }

        public static Font Font
        {
            get
            {
                if (s_font == null) s_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return s_font;
            }
        }

        public static Material TextMaterial
        {
            get
            {
                if (s_textMat == null)
                {
                    s_textMat = new Material(TextShader) { name = "WallInk Text" };
                    if (Font != null && Font.material != null) s_textMat.mainTexture = Font.material.mainTexture;
                    // The dynamic font atlas can be rebuilt as new characters are requested.
                    Font.textureRebuilt += f =>
                    {
                        if (f == s_font && s_textMat != null) s_textMat.mainTexture = f.material.mainTexture;
                    };
                }
                return s_textMat;
            }
        }

        /// <summary>Unit quad in the XY plane centred on the origin, UV 0..1. Vertex UV.x is 0 so the ink shader draws it solid.</summary>
        public static Mesh Quad
        {
            get
            {
                if (s_quad != null) return s_quad;
                s_quad = new Mesh { name = "WallInk Quad" };
                s_quad.SetVertices(new[]
                {
                    new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0),
                    new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0),
                });
                s_quad.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) });
                var white = new Color32(255, 255, 255, 255);
                s_quad.SetColors(new[] { white, white, white, white });
                s_quad.SetTriangles(new[] { 0, 2, 1, 1, 2, 3 }, 0);
                s_quad.RecalculateBounds();
                return s_quad;
            }
        }

        /// <summary>Solid quad for UI: same shape as <see cref="Quad"/> but UV.x = 0 everywhere (no edge fade).</summary>
        static Mesh s_solidQuad;
        public static Mesh SolidQuad
        {
            get
            {
                if (s_solidQuad != null) return s_solidQuad;
                s_solidQuad = Object.Instantiate(Quad);
                s_solidQuad.name = "WallInk Solid Quad";
                s_solidQuad.SetUVs(0, new[] { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero });
                return s_solidQuad;
            }
        }

        /// <summary>Unit-diameter anti-aliased disc in the XY plane.</summary>
        public static Mesh Disc
        {
            get
            {
                if (s_disc != null) return s_disc;
                var b = new MeshBuffers();
                b.Clear();
                var s = new StrokeData { space = StrokeSpace.Air, width = 1f, color = new Color32(255, 255, 255, 255), ribbonNormal = Vector3.back };
                s.points.Add(Vector3.zero);
                StrokeMeshBuilder.Append(b, s, 0f);
                s_disc = new Mesh { name = "WallInk Disc" };
                Upload(s_disc, b);
                return s_disc;
            }
        }

        public static void Upload(Mesh mesh, MeshBuffers b)
        {
            mesh.Clear();
            if (b.VertexCount == 0) return;
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(b.vertices);
            mesh.SetColors(b.colors);
            mesh.SetUVs(0, b.uvs);
            mesh.SetTriangles(b.indices, 0, false);
            // Bounds are tracked while building, which avoids a second pass over the vertices.
            Vector3 size = b.boundsMax - b.boundsMin;
            mesh.bounds = new Bounds((b.boundsMax + b.boundsMin) * 0.5f, size + Vector3.one * 1e-4f);
        }

        public static MeshRenderer NewRenderer(string name, Transform parent, Mesh mesh, Material material, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.sortingOrder = sortingOrder;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return mr;
        }

        static MaterialPropertyBlock s_block;
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public static void SetTint(Renderer r, Color c)
        {
            if (s_block == null) s_block = new MaterialPropertyBlock();
            r.GetPropertyBlock(s_block);
            s_block.SetColor(ColorId, c);
            r.SetPropertyBlock(s_block);
        }
    }
}
