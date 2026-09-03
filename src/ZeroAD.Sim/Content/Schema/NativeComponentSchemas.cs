using System.Collections.Generic;

namespace ZeroAD.Sim.Content.Schema;

/// <summary>原生(C++)组件的 GetSchema() 逐字移植(source/simulation2/components/CCmp*.cpp)。
/// 上游 grammar 里 JS 组件 schema 从数据树提取,原生组件硬编在引擎;本表是后者。
/// 冗长的 a:help/a:example 注解有删减(解析时注解即丢弃,不影响接受性);
/// 结构部分(element/attribute/choice/data/参数)与上游逐字一致。</summary>
public static class NativeComponentSchemas
{
    public static readonly IReadOnlyDictionary<string, string> All =
        new Dictionary<string, string>(System.StringComparer.Ordinal)
    {
        // ── 模板中实际出现的原生组件 ──

        ["Decay"] =
            "<element name='Active' a:help='If false, the entity will not do any decaying'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='SinkingAnim' a:help='If true, the entity will decay in a ship-like manner'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='SinkProb' a:help='The entity decays according to the supplied probability each frame.'>" +
                "<ref name='nonNegativeDecimal'/>" +
            "</element>" +
            "<element name='DelayTime' a:help='Time to wait before starting to sink, in seconds'>" +
                "<ref name='nonNegativeDecimal'/>" +
            "</element>" +
            "<element name='SinkRate' a:help='Initial rate of sinking, in meters per second'>" +
                "<ref name='nonNegativeDecimal'/>" +
            "</element>" +
            "<element name='SinkAccel' a:help='Acceleration rate of sinking, in meters per second per second'>" +
                "<ref name='nonNegativeDecimal'/>" +
            "</element>",

        ["Footprint"] =
            "<choice>" +
                "<element name='Square' a:help='Set the footprint to a square of the given size'>" +
                    "<attribute name='width'>" +
                        "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                    "</attribute>" +
                    "<attribute name='depth'>" +
                        "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                    "</attribute>" +
                "</element>" +
                "<element name='Circle' a:help='Set the footprint to a circle of the given size'>" +
                    "<attribute name='radius'>" +
                        "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                    "</attribute>" +
                "</element>" +
            "</choice>" +
            "<element name='Height' a:help='Vertical extent of the footprint (in meters)'>" +
                "<ref name='nonNegativeDecimal'/>" +
            "</element>" +
            "<optional>" +
                "<element name='MaxSpawnDistance' a:help='Farthest distance units can spawn away from the edge of this entity'>" +
                    "<ref name='nonNegativeDecimal'/>" +
                "</element>" +
            "</optional>",

        ["Minimap"] =
            "<optional>" +
                "<element name='Color'>" +
                    "<attribute name='r'>" +
                        "<data type='integer'><param name='minInclusive'>0</param><param name='maxInclusive'>255</param></data>" +
                    "</attribute>" +
                    "<attribute name='g'>" +
                        "<data type='integer'><param name='minInclusive'>0</param><param name='maxInclusive'>255</param></data>" +
                    "</attribute>" +
                    "<attribute name='b'>" +
                        "<data type='integer'><param name='minInclusive'>0</param><param name='maxInclusive'>255</param></data>" +
                    "</attribute>" +
                "</element>" +
            "</optional>" +
            "<optional>" +
                "<element name='Icon' a:help='Icon texture that should be displayed on a minimap.'>" +
                    "<attribute name='size'>" +
                        "<data type='float'><param name='minExclusive'>0</param></data>" +
                    "</attribute>" +
                    "<text/>" +
                "</element>" +
            "</optional>",

        ["Obstruction"] =
            "<choice>" +
                "<element name='Static'>" +
                    "<attribute name='width'>" +
                        "<data type='decimal'><param name='minInclusive'>1.5</param></data>" +
                    "</attribute>" +
                    "<attribute name='depth'>" +
                        "<data type='decimal'><param name='minInclusive'>1.5</param></data>" +
                    "</attribute>" +
                "</element>" +
                "<element name='Unit'>" +
                    "<empty/>" +
                "</element>" +
                "<element name='Obstructions'>" +
                    "<zeroOrMore>" +
                        "<element>" +
                            "<anyName/>" +
                            "<optional>" +
                                "<attribute name='x'><data type='decimal'/></attribute>" +
                            "</optional>" +
                            "<optional>" +
                                "<attribute name='z'><data type='decimal'/></attribute>" +
                            "</optional>" +
                            "<attribute name='width'>" +
                                "<data type='decimal'><param name='minInclusive'>1.5</param></data>" +
                            "</attribute>" +
                            "<attribute name='depth'>" +
                                "<data type='decimal'><param name='minInclusive'>1.5</param></data>" +
                            "</attribute>" +
                        "</element>" +
                    "</zeroOrMore>" +
                "</element>" +
            "</choice>" +
            "<element name='Active'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='BlockMovement'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='BlockPathfinding'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='BlockFoundation'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='BlockConstruction'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='DeleteUponConstruction'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='DisableBlockMovement'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='DisableBlockPathfinding'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<optional>" +
                "<element name='ControlPersist'>" +
                    "<empty/>" +
                "</element>" +
            "</optional>",

        ["Ownership"] = "<empty/>",

        ["Position"] =
            "<element name='Anchor' a:help='Automatic rotation to follow the slope of terrain'>" +
                "<choice>" +
                    "<value a:help='Always stand straight up (e.g. humans)'>upright</value>" +
                    "<value a:help='Rotate backwards and forwards to follow the terrain (e.g. animals)'>pitch</value>" +
                    "<value a:help='Rotate sideways to follow the terrain'>roll</value>" +
                    "<value a:help='Rotate in all directions to follow the terrain (e.g. carts)'>pitch-roll</value>" +
                "</choice>" +
            "</element>" +
            "<element name='Altitude' a:help='Height above terrain in meters'>" +
                "<data type='decimal'/>" +
            "</element>" +
            "<element name='Floating' a:help='Whether the entity floats on water'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='FloatDepth' a:help='The depth at which an entity floats on water (needs Floating to be true)'>" +
                "<ref name='nonNegativeDecimal'/>" +
            "</element>" +
            "<element name='TurnRate' a:help='Maximum rotation speed around Y axis, in radians per second.'>" +
                "<ref name='positiveDecimal'/>" +
            "</element>",

        ["RallyPointRenderer"] =
            "<element name='MarkerTemplate' a:help='Template name for the rally point marker entity'>" +
                "<text/>" +
            "</element>" +
            "<element name='LineTexture' a:help='Texture file to use for the rally point line'>" +
                "<text />" +
            "</element>" +
            "<element name='LineTextureMask' a:help='Texture mask to indicate where overlay colors are to be applied'>" +
                "<text />" +
            "</element>" +
            "<element name='LineThickness' a:help='Thickness of the marker line connecting the entity to the rally point marker'>" +
                "<data type='decimal'/>" +
            "</element>" +
            "<element name='LineDashColor'>" +
                "<attribute name='r'>" +
                    "<data type='integer'><param name='minInclusive'>0</param><param name='maxInclusive'>255</param></data>" +
                "</attribute>" +
                "<attribute name='g'>" +
                    "<data type='integer'><param name='minInclusive'>0</param><param name='maxInclusive'>255</param></data>" +
                "</attribute>" +
                "<attribute name='b'>" +
                    "<data type='integer'><param name='minInclusive'>0</param><param name='maxInclusive'>255</param></data>" +
                "</attribute>" +
            "</element>" +
            "<element name='LineStartCap'>" +
                "<choice>" +
                    "<value a:help='Abrupt line ending; line endings are not closed'>flat</value>" +
                    "<value a:help='Semi-circular line end cap'>round</value>" +
                    "<value a:help='Sharp, pointy line end cap'>sharp</value>" +
                    "<value a:help='Square line end cap'>square</value>" +
                "</choice>" +
            "</element>" +
            "<element name='LineEndCap'>" +
                "<choice>" +
                    "<value a:help='Abrupt line ending; line endings are not closed'>flat</value>" +
                    "<value a:help='Semi-circular line end cap'>round</value>" +
                    "<value a:help='Sharp, pointy line end cap'>sharp</value>" +
                    "<value a:help='Square line end cap'>square</value>" +
                "</choice>" +
            "</element>" +
            "<element name='LinePassabilityClass' a:help='The pathfinder passability class to use for computing the rally point marker line path'>" +
                "<text />" +
            "</element>",

        ["Selectable"] =
            "<optional>" +
                "<element name='EditorOnly' a:help='If this element is present, the entity is only selectable in Atlas'>" +
                    "<empty/>" +
                "</element>" +
            "</optional>" +
            "<element name='Overlay' a:help='Specifies the type of overlay to be displayed when this entity is selected.'>" +
                "<interleave>" +
                    "<optional>" +
                        "<element name='Shape' a:help='Specifies shape of overlay.'>" +
                            "<choice>" +
                                "<element name='Square' a:help='Set the overlay to a square of the given size'>" +
                                    "<attribute name='width'>" +
                                        "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                                    "</attribute>" +
                                    "<attribute name='depth'>" +
                                        "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                                    "</attribute>" +
                                "</element>" +
                                "<element name='Circle' a:help='Set the overlay to a circle of the given size'>" +
                                    "<attribute name='radius'>" +
                                        "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                                    "</attribute>" +
                                "</element>" +
                            "</choice>" +
                        "</element>" +
                    "</optional>" +
                    "<optional>" +
                        "<element name='AlwaysVisible' a:help='If this element is present, the selection overlay will always be visible'>" +
                            "<empty/>" +
                        "</element>" +
                    "</optional>" +
                    "<choice>" +
                        "<element name='Texture' a:help='Displays a texture underneath the entity.'>" +
                            "<element name='MainTexture' a:help='Texture to display underneath the entity.'><text/></element>" +
                            "<element name='MainTextureMask' a:help='Mask texture that controls where to apply player color.'><text/></element>" +
                        "</element>" +
                        "<element name='Outline' a:help='Traces the outline of the entity with a line texture.'>" +
                            "<element name='LineTexture' a:help='Texture to apply to the line.'><text/></element>" +
                            "<element name='LineTextureMask' a:help='Texture that controls where to apply player color.'><text/></element>" +
                            "<element name='LineThickness' a:help='Thickness of the line, in world units.'><ref name='positiveDecimal'/></element>" +
                        "</element>" +
                    "</choice>" +
                "</interleave>" +
            "</element>",

        ["TerritoryInfluence"] =
            "<element name='Root'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='Weight'>" +
                "<data type='nonNegativeInteger'>" +
                    "<param name='maxInclusive'>65535</param>" +
                "</data>" +
            "</element>" +
            "<element name='Radius'>" +
                "<data type='nonNegativeInteger'/>" +
            "</element>",

        ["UnitMotion"] =
            "<element name='FormationController'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='WalkSpeed' a:help='Basic movement speed (in meters per second).'>" +
                "<ref name='positiveDecimal'/>" +
            "</element>" +
            "<optional>" +
                "<element name='RunMultiplier' a:help='How much faster the unit goes when running (as a multiple of walk speed).'>" +
                    "<ref name='positiveDecimal'/>" +
                "</element>" +
            "</optional>" +
            "<element name='InstantTurnAngle' a:help='Angle we can turn instantly.'>" +
                "<ref name='positiveDecimal'/>" +
            "</element>" +
            "<element name='Acceleration' a:help='Acceleration (in meters per second^2).'>" +
                "<ref name='positiveDecimal'/>" +
            "</element>" +
            "<element name='PassabilityClass' a:help='Identifies the terrain passability class (values are defined in special/pathfinder.xml).'>" +
                "<text/>" +
            "</element>" +
            "<element name='Weight' a:help='Makes this unit both push harder and harder to push.'>" +
                "<ref name='positiveDecimal'/>" +
            "</element>" +
            "<optional>" +
                "<element name='DisablePushing'>" +
                    "<data type='boolean'/>" +
                "</element>" +
            "</optional>",

        ["Vision"] =
            "<element name='Range'>" +
                "<data type='nonNegativeInteger'/>" +
            "</element>" +
            "<optional>" +
                "<element name='RevealShore'>" +
                    "<data type='boolean'/>" +
                "</element>" +
            "</optional>",

        ["VisualActor"] =
            "<element name='Actor' a:help='Filename of the actor to be used for this unit'>" +
                "<text/>" +
            "</element>" +
            "<optional>" +
                "<element name='FoundationActor' a:help='Filename of the actor to be used the foundation while this unit is being constructed'>" +
                    "<text/>" +
                "</element>" +
            "</optional>" +
            "<optional>" +
                "<element name='Foundation' a:help='Used internally; if present, the unit will be rendered as a foundation'>" +
                    "<empty/>" +
                "</element>" +
            "</optional>" +
            "<optional>" +
                "<element name='ConstructionPreview' a:help='If present, the unit should have a construction preview'>" +
                    "<empty/>" +
                "</element>" +
            "</optional>" +
            "<optional>" +
                "<element name='ActorOnly' a:help='Used internally; if present, the unit will only be rendered if the user has high enough graphical settings.'>" +
                    "<empty/>" +
                "</element>" +
            "</optional>" +
            "<element name='SilhouetteDisplay'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<element name='SilhouetteOccluder'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<optional>" +
                "<element name='SelectionShape'>" +
                    "<choice>" +
                        "<element name='Bounds' a:help='Determines the selection box based on the model bounds'>" +
                            "<empty/>" +
                        "</element>" +
                        "<element name='Footprint' a:help='Determines the selection box based on the entity Footprint component'>" +
                            "<empty/>" +
                        "</element>" +
                        "<element name='Box' a:help='Sets the selection shape to a box of specified dimensions'>" +
                            "<attribute name='width'>" +
                                "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                            "</attribute>" +
                            "<attribute name='height'>" +
                                "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                            "</attribute>" +
                            "<attribute name='depth'>" +
                                "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                            "</attribute>" +
                        "</element>" +
                        "<element name='Cylinder' a:help='Sets the selection shape to a cylinder of specified dimensions'>" +
                            "<attribute name='radius'>" +
                                "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                            "</attribute>" +
                            "<attribute name='height'>" +
                                "<data type='decimal'><param name='minExclusive'>0.0</param></data>" +
                            "</attribute>" +
                        "</element>" +
                    "</choice>" +
                "</element>" +
            "</optional>" +
            "<element name='VisibleInAtlasOnly'>" +
                "<data type='boolean'/>" +
            "</element>" +
            "<optional>" +
                "<element name='ShadowsCast' a:help='If true (default), the entity will cast dynamic shadows onto the environment.'>" +
                    "<data type='boolean'/>" +
                "</element>" +
            "</optional>" +
            "<optional>" +
                "<element name='ShadowsReceive' a:help='If true (default), the entity will receive dynamic shadows from other objects.'>" +
                    "<data type='boolean'/>" +
                "</element>" +
            "</optional>",

        // ── 模板里出现但 schema 为空的原生组件 ──

        ["OverlayRenderer"] = "<empty/>",
        ["RangeOverlayRenderer"] = "<empty/>",

        // ── 系统/测试组件(上游同样在 grammar 注册;模板不出现,parity 收录)──

        ["TemplateManager"] = "<a:component type='system'/><empty/>",
        ["Terrain"] = "<a:component type='system'/><empty/>",
        ["TerritoryManager"] = "<a:component type='system'/><empty/>",
        ["WaterManager"] = "<a:component type='system'/><empty/>",
        ["SoundManager"] = "<a:component type='system'/><empty/>",
        ["RangeManager"] = "<a:component type='system'/><empty/>",
        ["ObstructionManager"] = "<a:component type='system'/><empty/>",
        ["CommandQueue"] = "<a:component type='system'/><empty/>",
        ["AIManager"] = "<a:component type='system'/><empty/>",
        ["CinemaManager"] = "<a:component type='system'/><empty/>",
        ["ParticleManager"] = "<a:component type='system'/><empty/>",
        ["Pathfinder"] = "<a:component type='system'/><empty/>",
        ["UnitMotionManager"] = "<a:component type='system'/><empty/>",
        ["UnitRenderer"] = "<a:component type='system'/><empty/>",
        ["ProjectileManager"] = "<a:component type='system'/><empty/>",
        ["Test"] = "<a:component type='test'/><ref name='anything'/>",
        ["MotionBall"] = "<a:component type='test'/><ref name='anything'/>",
    };
}
