using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;
using PowerPotion.Protocol;
using PowerPotion.Revit.Export;
using PowerPotion.Revit.Sync;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace PowerPotion.Revit.UI;

public partial class MaterialEditorWindow : System.Windows.Window
{
    private readonly Document _doc;
    private readonly RevitMaterialParser _parser;
    private readonly Dictionary<string, ElementId> _materialsByName = new();
    private MaterialDto? _current;
    private bool _suppress;

    public MaterialEditorWindow(Document doc)
    {
        _doc = doc;
        _parser = new RevitMaterialParser(doc, LiveSyncService.TextureSearchPaths(doc)) { Log = m => System.Diagnostics.Debug.WriteLine("[PowerPotion material] " + m) };
        InitializeComponent();
        ApplyDarkTitleBar();
        LoadMaterials();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            var value = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }
        catch { }
    }

    private void LoadMaterials()
    {
        var materials = new FilteredElementCollector(_doc)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .Where(m => !string.IsNullOrEmpty(m.Name))
            .OrderBy(m => m.Name);

        _suppress = true;
        foreach (var m in materials)
        {
            _materialsByName[m.Name] = m.Id;
            MaterialList.Items.Add(m.Name);
        }
        _suppress = false;
        if (MaterialList.Items.Count > 0)
            MaterialList.SelectedIndex = 0;
    }

    private void OnMaterialSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MaterialList.SelectedItem is not string name) return;
        if (!_materialsByName.TryGetValue(name, out var id)) return;
        var dto = _parser.GetOrParse(id);
        if (dto == null) return;
        _current = dto;

        _suppress = true;
        MaterialName.Text = dto.Name;
        Red.Value = dto.DiffuseR;
        Green.Value = dto.DiffuseG;
        Blue.Value = dto.DiffuseB;
        Roughness.Value = dto.Roughness;
        Metallic.Value = dto.Metallic;
        Opacity.Value = dto.Opacity;
        Emissive.Value = dto.EmissiveStrength;
        TexturePath.Text = dto.DiffuseTexture.File;
        _suppress = false;
    }

    private void OnSliderChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || _current == null) return;
        _current.DiffuseR = (float)Red.Value;
        _current.DiffuseG = (float)Green.Value;
        _current.DiffuseB = (float)Blue.Value;
        _current.Roughness = (float)Roughness.Value;
        _current.Metallic = (float)Metallic.Value;
        _current.Opacity = (float)Opacity.Value;
        _current.EmissiveStrength = (float)Emissive.Value;
    }

    private void OnBrowseTexture(object sender, System.Windows.RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select albedo texture",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true && _current != null)
        {
            _current.DiffuseTexture = TextureSlotDto.Identity(dlg.FileName);
            TexturePath.Text = dlg.FileName;
        }
    }

    private void OnWriteBack(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_current == null) return;
        if (MaterialList.SelectedItem is not string name) return;
        if (!_materialsByName.TryGetValue(name, out var materialId)) return;

        try
        {
            if (_doc.GetElement(materialId) is not Material material) return;

            using var tx = new Transaction(_doc, "PowerPotion: update material");
            tx.Start();

            material.Color = new Color(
                (byte)(Red.Value * 255), (byte)(Green.Value * 255), (byte)(Blue.Value * 255));
            material.Transparency = (byte)Math.Round((1.0 - Opacity.Value) * 100.0);

            var assetId = material.AppearanceAssetId;
            if (assetId != ElementId.InvalidElementId)
            {
                using var editScope = new AppearanceAssetEditScope(_doc);
                var editableAsset = editScope.Start(assetId);

                AssignColor(editableAsset, "generic_diffuse", Red.Value, Green.Value, Blue.Value);
                AssignDouble(editableAsset, "generic_glossiness", Math.Clamp(1.0 - Roughness.Value, 0.01, 1.0));
                AssignDouble(editableAsset, "generic_transparency", Math.Clamp(1.0 - Opacity.Value, 0.0, 0.98));
                AssignBoolean(editableAsset, "generic_is_metal", Metallic.Value > 0.5);

                editScope.Commit(true);
            }

            tx.Commit();
            TaskDialog.Show("PowerPotion", $"Material '{name}' updated in the document.");
        }
        catch (Exception ex)
        {
            TaskDialog.Show("PowerPotion", "Write-back failed: " + ex.Message);
        }
    }

    private static void AssignColor(Asset asset, string name, double r, double g, double b)
    {
        if (asset.FindByName(name) is AssetPropertyDoubleArray4d color)
            color.SetValueAsColor(new Color((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
    }

    private static void AssignDouble(Asset asset, string name, double value)
    {
        if (asset.FindByName(name) is AssetPropertyDouble d)
            d.Value = value;
    }

    private static void AssignBoolean(Asset asset, string name, bool value)
    {
        if (asset.FindByName(name) is AssetPropertyBoolean b)
            b.Value = value;
    }

    private void OnClose(object sender, System.Windows.RoutedEventArgs e) => Close();
}
