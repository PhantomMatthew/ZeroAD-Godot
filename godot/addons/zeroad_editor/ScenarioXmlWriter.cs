using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using Godot;

namespace ZeroAD.Godot.Editor;

/// <summary>Scenario XML 写入器（镜像 MapWriter::WriteXML）。
/// 输出 0 A.D. 的 Scenario version="7" XML 格式：
///   &lt;Scenario&gt; → &lt;Environment&gt; + &lt;Camera&gt; + &lt;ScriptSettings&gt;(JSON CDATA) + &lt;Entities&gt;
/// 实体数据从场景树的 Node3D 子节点收集。</summary>
[Tool]
public static class ScenarioXmlWriter
{
    /// <summary>保存场景为 Scenario XML。</summary>
    public static void Save(string path, MapData data, List<MapEntityData> entities)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            Indent = true,
            IndentChars = "\t",
            OmitXmlDeclaration = false,
        };

        using var sw = new StreamWriter(path, false, Encoding.UTF8);
        using var xw = XmlWriter.Create(sw, settings);

        xw.WriteStartDocument();
        xw.WriteStartElement("Scenario");
        xw.WriteAttributeString("version", "7");

        // <Environment>（简化：默认值）
        xw.WriteStartElement("Environment");
        xw.WriteStartElement("SkySet"); xw.WriteString("default"); xw.WriteEndElement();
        xw.WriteStartElement("SunColour"); WriteColor(xw, 0.749f, 0.749f, 0.749f); xw.WriteEndElement();
        xw.WriteStartElement("SunElevation"); WriteFloat(xw, 0.785f); xw.WriteEndElement();
        xw.WriteStartElement("SunRotation"); WriteFloat(xw, 4.712f); xw.WriteEndElement();
        xw.WriteStartElement("TerrainAmbientColour"); WriteColor(xw, 0.50196f, 0.50196f, 0.50196f); xw.WriteEndElement();
        xw.WriteStartElement("UnitsAmbientColour"); WriteColor(xw, 0.50196f, 0.50196f, 0.50196f); xw.WriteEndElement();
        xw.WriteStartElement("Water"); xw.WriteAttributeString("r", "0.294"); xw.WriteAttributeString("g", "0.349"); xw.WriteAttributeString("b", "0.694"); xw.WriteEndElement();
        xw.WriteEndElement();  // Environment

        // <Camera>（默认俯视）
        xw.WriteStartElement("Camera");
        xw.WriteStartElement("Position"); WriteFloat3(xw, 288f, 214.5f, 174.5f); xw.WriteEndElement();
        xw.WriteStartElement("Rotation"); WriteFloatAttr(xw, "angle", 0f); xw.WriteEndElement();
        xw.WriteEndElement();  // Camera

        // <ScriptSettings>（JSON CDATA）
        xw.WriteStartElement("ScriptSettings");
        var json = BuildScriptSettingsJson(data);
        xw.WriteCData(json);
        xw.WriteEndElement();

        // <Entities>
        xw.WriteStartElement("Entities");
        int uid = 150;
        foreach (var ent in entities)
        {
            xw.WriteStartElement("Entity");
            xw.WriteAttributeString("uid", (ent.Uid > 0 ? ent.Uid : uid++).ToString());
            xw.WriteStartElement("Template"); xw.WriteString(ent.Template); xw.WriteEndElement();
            xw.WriteStartElement("Player"); xw.WriteString(ent.PlayerID.ToString()); xw.WriteEndElement();
            xw.WriteStartElement("Position");
            xw.WriteAttributeString("x", ent.X.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            xw.WriteAttributeString("y", ent.Y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            xw.WriteAttributeString("z", ent.Z.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            xw.WriteEndElement();
            xw.WriteStartElement("Orientation");
            xw.WriteAttributeString("y", ent.Angle.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            xw.WriteEndElement();
            xw.WriteEndElement();  // Entity
        }
        xw.WriteEndElement();  // Entities

        xw.WriteEndElement();  // Scenario
        xw.WriteEndDocument();
    }

    private static string BuildScriptSettingsJson(MapData data)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"Name\": \"").Append(EscapeJson(data.MapName)).Append("\",\n");
        sb.Append("  \"Description\": \"").Append(EscapeJson(data.Description)).Append("\",\n");
        sb.Append("  \"PlayerData\": [\n    null,\n");  // index 0 = gaia
        for (int i = 0; i < data.Players.Count; i++)
        {
            var p = data.Players[i];
            sb.Append("    {");
            sb.Append("\"Civ\":\"").Append(p.Civ).Append("\"");
            sb.Append(",\"Color\":{\"r\":").Append(p.Color.Length > 0 ? "1" : "1");
            sb.Append(",\"g\":1,\"b\":1}");
            sb.Append(",\"Team\":").Append(p.Team);
            sb.Append(",\"Resources\":{\"food\":").Append(p.Food);
            sb.Append(",\"wood\":").Append(p.Wood);
            sb.Append(",\"stone\":").Append(p.Stone);
            sb.Append(",\"metal\":").Append(p.Metal);
            sb.Append("}}");
            if (i < data.Players.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ],\n");
        sb.Append("  \"VictoryConditions\": [\"conquest\"]\n");
        sb.Append("}");
        return sb.ToString();
    }

    private static void WriteColor(XmlWriter xw, float r, float g, float b)
    {
        xw.WriteAttributeString("r", r.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        xw.WriteAttributeString("g", g.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        xw.WriteAttributeString("b", b.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    }
    private static void WriteFloat(XmlWriter xw, float v)
        => xw.WriteAttributeString("angle", v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    private static void WriteFloatAttr(XmlWriter xw, string name, float v)
        => xw.WriteAttributeString(name, v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    private static void WriteFloat3(XmlWriter xw, float x, float y, float z)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        xw.WriteAttributeString("x", x.ToString("F1", ci));
        xw.WriteAttributeString("y", y.ToString("F1", ci));
        xw.WriteAttributeString("z", z.ToString("F1", ci));
    }
    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
