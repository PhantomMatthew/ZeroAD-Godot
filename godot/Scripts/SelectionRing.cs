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

    public enum Shape { Circle, Square }

    /// <summary>
    /// Selection marker matching the original: units get a circular ring,
    /// buildings get a square outline around their footprint.
    /// </summary>
    public static MeshInstance3D Create(float radius, Color friendlyColor, Color enemyColor,
        Shape shape = Shape.Circle)
    {
        EnsureMaterials(friendlyColor, enemyColor);

        var points = shape == Shape.Square ? SquarePoints(radius) : CirclePoints(radius);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.LineStrip);
        foreach (var p in points)
            st.AddVertex(p);
        var mesh = st.Commit();
        var instance = new MeshInstance3D { Mesh = mesh };
        mesh.SurfaceSetMaterial(0, _ringMat);
        return instance;
    }

    private static Vector3[] SquarePoints(float half) => new Vector3[]
    {
        new(-half, 0.1f, -half),
        new(half, 0.1f, -half),
        new(half, 0.1f, half),
        new(-half, 0.1f, half),
        new(-half, 0.1f, -half),
    };

    private static Vector3[] CirclePoints(float radius)
    {
        const int segments = 32;
        var pts = new Vector3[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float a = i * Mathf.Tau / segments;
            pts[i] = new Vector3(Mathf.Cos(a) * radius, 0.1f, Mathf.Sin(a) * radius);
        }
        return pts;
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
