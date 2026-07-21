using Godot;

namespace ZeroAD.Godot;

public static class SelectionRing
{
    private static StandardMaterial3D _ringMat = null!;
    private static StandardMaterial3D _ringMatEnemy = null!;

    private static StandardMaterial3D CreateRingMat(Color color)
    {
        var mat = new StandardMaterial3D();
        mat.AlbedoColor = color;
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        mat.NoDepthTest = true;
        return mat;
    }

    private static void EnsureMaterials(Color friendlyColor, Color enemyColor)
    {
        _ringMat ??= CreateRingMat(friendlyColor);
        _ringMatEnemy ??= CreateRingMat(enemyColor);
    }

    public static MeshInstance3D Create(float radius, Color friendlyColor, Color enemyColor)
    {
        EnsureMaterials(friendlyColor, enemyColor);

        float half = radius;

        var points = new Vector3[]
        {
            new(-half, 0.1f, -half),
            new(half, 0.1f, -half),
            new(half, 0.1f, half),
            new(-half, 0.1f, half),
            new(-half, 0.1f, -half),
        };

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.LineStrip);
        foreach (var p in points)
            st.AddVertex(p);
        var mesh = st.Commit();
        var instance = new MeshInstance3D { Mesh = mesh };
        mesh.SurfaceSetMaterial(0, _ringMat);
        return instance;
    }

    public static MeshInstance3D CreateHealthBar(float healthFraction)
    {
        float w = 2f;
        float h = 0.3f;
        float greenW = w * Mathf.Clamp(healthFraction, 0f, 1f);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        st.SetColor(new Color(0.1f, 0.7f, 0.1f));
        st.AddVertex(new Vector3(-w / 2, 0, 0));
        st.AddVertex(new Vector3(-w / 2 + greenW, 0, 0));
        st.AddVertex(new Vector3(-w / 2 + greenW, h, 0));
        st.AddVertex(new Vector3(-w / 2, 0, 0));
        st.AddVertex(new Vector3(-w / 2 + greenW, h, 0));
        st.AddVertex(new Vector3(-w / 2, h, 0));

        if (greenW < w)
        {
            st.SetColor(new Color(0.7f, 0.1f, 0.1f));
            st.AddVertex(new Vector3(-w / 2 + greenW, 0, 0));
            st.AddVertex(new Vector3(w / 2, 0, 0));
            st.AddVertex(new Vector3(w / 2, h, 0));
            st.AddVertex(new Vector3(-w / 2 + greenW, 0, 0));
            st.AddVertex(new Vector3(w / 2, h, 0));
            st.AddVertex(new Vector3(-w / 2 + greenW, h, 0));
        }

        var mesh = st.Commit();
        var instance = new MeshInstance3D { Mesh = mesh };
        var mat = new StandardMaterial3D();
        mat.VertexColorUseAsAlbedo = true;
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mat.NoDepthTest = true;
        mesh.SurfaceSetMaterial(0, mat);
        instance.Position = new Vector3(0, 4f, 0);
        return instance;
    }
}
