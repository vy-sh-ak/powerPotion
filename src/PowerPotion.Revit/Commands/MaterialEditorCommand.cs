using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PowerPotion.Revit.Export;
using PowerPotion.Revit.UI;

namespace PowerPotion.Revit.Commands;

/// <summary>
/// Opens the PowerPotion material editor: lists document materials with their parsed
/// rendering parameters (color/roughness/metallic/opacity/emission), previews edits in
/// the live renderer and writes changes back to the Revit appearance asset.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class MaterialEditorCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc == null) return Result.Failed;

        var window = new MaterialEditorWindow(doc)
        {
            Owner = null,
        };
        window.ShowDialog();
        return Result.Succeeded;
    }
}
