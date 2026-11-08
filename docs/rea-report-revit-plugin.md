All analysis is complete. I have comprehensive evidence for every requested subsystem. Here is the technical report.

---

# Enscape 4.7.0.57 Revit 2026 Plugin — Scene-Extraction Analysis (Clean-Room Reimplementation Report)

**Target:** `C:\Program Files\Enscape\Enscape.Revit.Plugin-net8.0-windows7.0\Enscape.Revit.Plugin.2026.dll` (assembly `Enscape.Revit.Plugin.2026`, v4.7.0.57, .NET 8, MVID `5542530d-af47-4012-a8a4-b028d73f1587`) and `Enscape.Cad.Base.dll` (v4.7.0.57, MVID `c88ec20b-6f64-498a-af14-4524e18bc357`), plus `Enscape.Cad.Abstractions.dll`.

**Method:** Static metadata/CIL inspection only (no loading, no decompilation, no execution). Claims below are derived from decoded method signatures, resolved call edges (`call`/`callvirt`/`newobj` targets), field lists, and `ldstr` string literals tied to methods via instruction anchors. Tokens are build-local; string-literal attribution per method is exact.

**Primary evidence IDs** (all `ev_*` are retained REA Evidence records, SHA-256-bound to the artifact):
- Plugin full-member Evidence: `ev_5d78d938f081d4524a39d753d00abe22d458528a0bc6c3d37dd4933ec7969901` (463 types, 2102 methods, 11697 call edges, 587 user strings; all 587 quoted verbatim below were read from it)
- Plugin assembly refs (managed artifact view): `ev_f4c01dde28c8727524d920dbc565879eafc13f1e320a23fa4470e5c438527272`
- Cad.Base full-member Evidence: `ev_2416c8b0f8e05e430f1d30acc277087399228404066262c53fd346217141d5c3`
- Cad.Abstractions type catalog: `ev_84e764aca71b1ba78b70d30d6cb1300328c2cbe36481d6316c0c0efb6aba6543`
- Per-subgroup Evidence: export core `ev_8620aa52…`, orchestrator `ev_a671768f…`, geometry `ev_bbfdedb7…`, lights `ev_8f8b35bc…`, sun/site `ev_c28c2c88…`, MaterialParser `ev_b5f49af0…`, material support `ev_35335cb4…`, MaterialEditor `ev_e7e292a3…`, GraphicsOverrides `ev_78743053…`, ribbon `ev_b7f6665e…`, camera `ev_6308a133…`, view/observer/multiversion `ev_9fda1b83…`, Cad.Base world model `ev_0e304495…`, Cad.Base materials/FlatWorld `ev_0286ed50…`.

**Key platform facts (observed):** The plugin references **RevitAPI 26.0.4.0** and **RevitAPIUI 26.0.4.0** (Revit 2026), .NET 8, WPF (`PresentationCore/WindowsBase/PresentationFramework`), `Enscape.Cad.Base`/`Enscape.Cad.Remote`/`Enscape.Cad.Abstractions` 4.7.0.57, `Enscape.Core`, `GlmSharpStd` (GLM math), `Newtonsoft.Json`. Cad.Base references **no Revit API at all** (CAD-agnostic), plus SkiaSharp and log4net.

---

## 0. Architecture Overview (observed)

```
Revit DocumentChanged/Idling events
        │
        ▼
DocumentObserver (Enscape.Revit.Plugin.RenderingSync)         [ev_9fda1b83]
  builds RevitTransaction {Added/Modified/Deleted ElementId sets, NeedsFullExport}
        │ full export: RevitTransactionExecutor.ExecuteFullUpdate
        │ diff export: ExecuteDiffTransaction(…, RevitExportedWorld previous)
        ▼
RevitGeometryParser.Export(View3D, ExportCache, progress, token)   [ev_bbfdedb7]
  ctx = ExportContextFactory.Create(view, cache, token)             (DI container)
  new Autodesk.Revit.DB.CustomExporter(document, ctx)
  exporter.ShouldStopOnError = false
  RevitMultiVersion.ExportView(exporter, uiApp, view3D, ctx, logger)
      → Autodesk.Revit.DB.CustomExporter.Export(view)               [ev_9fda1b83]
        ▼ (Revit walks the view and calls back into…)
ExportContext : Autodesk.Revit.DB IExportContext                    [ev_8620aa52]
  OnElementBegin/OnInstanceBegin/OnLinkBegin/OnFaceBegin/OnRPC/OnLight/OnMaterial/OnDecals/OnPolymesh
  ├─ GeometryCollector (per element/link) → ExportedFace (triangulated)
  ├─ LightParser.ParseLight(ContentNode) → ExportedComplexLight
  ├─ MaterialParser + AssetFinder + MaterialSourceSelector + MaterialCombiner → WorldGraphMaterial
  ├─ SoundParser / EnscapeAssetManager → ExportedSound / ExportedAsset
  └─ writes into ExportCache { Dictionary<ExportId, ExportedModelDefinition>,
                               Dictionary<ExportId, WorldGraphMaterial>, DecalCache }
        ▼
ExportCache.ToExportedWorld() → RevitExportedWorld : Enscape.Cad.ExportedWorld.ExportedWorld
        ▼
IModifiableWorldTree.Set(world) / IModifiableMaterialsCollection.Set(...)   (Enscape.WorldExport, Cad.Base)
GraphicsOverrideProcessor.ApplyViewOverridesToWorld(view3D, world)  (clones + rewrites materials)
        ▼
FlatWorldContext + FlatWorldConverterFromExportedWorld (Cad.Base)
  → FlatWorldHandle → ISynchronizedLiveViewport.SetWorld / StartExport / FinishExport  (Enscape.Cad.Remote)
```

Full export and incremental (diff) export are the same pipeline; diff simply pre-populates `ExportCache` from the previous `RevitExportedWorld` and re-exports only changed subtrees (`PrepareExportCache`, `ParseModifiedMaterials`), then `ExportCache.ToExportedWorld()` again `[ev_a671768f]`.

---

## 1. Export core: `ExportContext`, `RevitExportedWorld`, `RevitRenderingSync`, `RevitModifiableWorldTree`

**Evidence:** `ev_8620aa52…` (ExportContext: 37 methods + nested), `ev_a671768f…` (orchestrator), `ev_9fda1b83…` (view/observer).

### 1.1 `ExportContext` — a Revit `CustomExporter` IExportContext (observed)

`Enscape.Revit.Plugin.RenderingSync.ExportContext` implements the full `Autodesk.Revit.DB.IExportContext` callback surface (method names observed exactly): `Start`, `Finish`, `IsCanceled`, `OnViewBegin(ViewNode)`, `OnViewEnd`, `OnElementBegin/End`, `OnInstanceBegin/End(InstanceNode)`, `OnLinkBegin/End(LinkNode)`, `OnFaceBegin/End(FaceNode)`, `OnRPC(ContentNode)`, `OnLight(ContentNode)`, `OnMaterial(MaterialNode)`, `OnDecals(MaterialNode)`, `OnPolymesh(PolymeshTopology)`.

Key members (fields, observed):
- `_cache : ExportCache`, `_document : Autodesk.Revit.DB.Document`, `_currentDocument`, `_currentElement : Autodesk.Revit.DB.Element`, `_currentFace`, `_currentLink`, `_currentMaterialId`, `_currentDecals[]`
- `_geometryStack : Stack<GeometryCollector>`, `_instanceDefinitionStack`, `_instanceDefinitionCache : InstanceDefinitionCache`, `_missingMaterialIds`
- `_materialParser`, `_lightParser`, `_soundParser`, `_assetManager`, `_materialSourceSelector`, `_revitLiveCamera`, `_logger`, `_progress`, `_cancellationToken`
- `HandFlipTransform`, `FacingFlipTransform : GlmSharp.dmat4` (static, built via `dmat4.Scale` in `.cctor` — mirror compensation for flipped family instances), `LinkedInstanceIdSeparator : string` (value `"_"` observed as a literal used in `OnElementEnd`).

Callback semantics (from resolved call edges):
- **OnElementBegin**: creates a fresh `GeometryCollector`, `CreateId(element)` (→ `ExportId` keyed by `ElementIdInLinkedProject{LinkedProjectId, ElementId}`), skips via `IsIgnored`/`IsLiveCamera`/`TryParseSoundSource`; `Document.GetElement`.
- **IsIgnored**: `Element.get_Category` → `Category.get_CategoryType`, `Element.get_ParametersMap` + `ParameterMap.Contains("BIM Track Clash Guid")` (BIM Track clash elements are skipped).
- **OnElementEnd**: if `GeometryCollector.HasGeometry` → `ToModelDefinition()` → `ExportedModelDefinition.<Clone>$`, sets `Id`, `PersistentId = Element.get_UniqueId`, creates `ExportedModelInstance` (`Id`, `PersistentId`, `Definition`, `IsVisible`, `IsLayerVisible`, `Transformation = dmat4.Identity` or instance transform, `FamilyClass = FamilyClassMapping.GetFromCategory(Category)`); handles `IsSingleRpcInstance` (RPC entourage bounding-box sizing via `Element.get_Geometry(Options)`, `GeometryElement.GetTransformed/GetBoundingBox`, `Instance.GetTransform`, `BoundingBoxXYZ.Min/Max` → `ExportedRpc.BbSize`, `IgnoreFitAabb`), `Element.GetTypeId` + `Document.GetElement` for type-based RPC lookup.
- **OnInstanceBegin/End** (family instances): `RevitMultiVersion.GetSymbolGeometryIdentifier(InstanceNode)` → `InstanceNode.GetSymbolGeometryId` → `SymbolGeometryId.get_SymbolId` / `AsUniqueIdentifier`; `GroupNode.GetTransform` → `TryConvertToMat4` (`TransformExtensions.ToGlm`); instance-definition caching via `InstanceDefinitionCache.GetInstanceDefinition/UpdateInstanceDefinition` (deduplicates repeated family symbols); `ExportedModelInstance` per instance with its transform.
- **OnLinkBegin/End** (linked documents): `LinkNode.GetDocument` (new `GeometryCollector` + `InstanceDefinitionCache.OnLinkBegin`), link instance gets `IsReadonly = true`, `PersistentId = UniqueId`, transform from `GroupNode.GetTransform`.
- **OnFaceBegin**: `FaceNode.GetFace`.
- **OnRPC**: `ContentNode.GetTransform` + `ContentNode.GetAsset` → `Autodesk.Revit.DB.Visual.AssetProperty.get_Name` → `ExportedRpc{Guid, HeightInFeet}` (`AssetPropertiesExtensions.GetHeightInFeet` reads asset property `"Height"`/`"RPCPlantHeight"`).
- **OnLight**: `LightParser.ParseLight` (see §3) + a wrapping `ExportedModelDefinition/Instance` with `ContentNode.GetTransform`.
- **OnMaterial**: `MaterialNode.get_HasOverriddenAppearance`, `MaterialNode.get_MaterialId`, `MaterialNode.GetAppearanceOverride` (delegates to `OnDecals` → `MaterialParser.ParseDecals` when the material carries a decal override); material resolution via `IMaterialSourceSelector.GetMaterialForWorld` (see §5); logs `"Material ", " not found in linked project ", …`.
- **OnPolymesh**: `Face.get_IsTwoSided`, `RevitGeometryParser.ParsePolymesh(PolymeshTopology)` → `ExportedFace.<Clone>$` + `set_MaterialFront/set_MaterialBack` (from current material id; `ExportId.NoMaterial` when none) + `set_Decals`.
- **TryParseAsset**: Enscape Asset-library objects: `EnscapeAssetManager.TryGetAssetData`, `ReadParametricOptions`, `ExtractScale`, `GetTransformation`, `FamilyInstance.get_HandFlipped/FacingFlipped`, `ComposeAssetTransformation` → `ExportedAsset{Id, Source}` + `AssetId`, `AssetParametricOptionsData`; `IsLinkedAsset` → `IsReadonly`.
- **TryParseSoundSource**: family name / `Element.get_ParametersMap` `"EnscapeSoundsource"` → `SoundParser.ParseSound` → `ExportedSound`.

### 1.2 Who calls CustomExporter (observed)

- `RevitTransactionExecutor.ExecuteFullUpdate(View3D, ISynchronizedExportProgress, CancellationToken)` → new `ExportCache` + `DecalCache` → **`RevitGeometryParser.Export(View3D, ExportCache, progress, token)`** → `ExportContextFactory.Create` → `new CustomExporter(document, exportContext)`, `set_ShouldStopOnError(false)` → `RevitMultiVersion.ExportView(...)` → **`CustomExporter.Export(view)`** → `ExportCache.ToExportedWorld()` `[ev_bbfdedb7, ev_9fda1b83]`.
- So the answer to the task question is: **yes, Revit's `CustomExporter`/`IExportContext` API is the geometry path; `FilteredElementCollector` is used only for diff bookkeeping** (type changes, MEP system membership, decal owners, link checks, design options, cameras, levels, materials listing) — see §1.4.

### 1.3 `RevitExportedWorld`, `RevitModifiableWorldTree` (observed)

- `RevitExportedWorld : Enscape.Cad.ExportedWorld.ExportedWorld` — adds exactly one member: `Decals : DecalCache` (plugin-side decal cache). Base `ExportedWorld` has `Geometry : ExportedModelInstance`, `Materials : WorldGraphMaterials`, `VRaySceneNames`, `ExtraData` (see §9).
- `RevitModifiableWorldTree : Enscape.WorldExport.ModifiableWorldTree` — adds `Decals` override on top of the Cad.Base tree (implements `Enscape.Revit.Plugin.RenderingSync.IRevitModifiableWorldTree`, which extends `Enscape.WorldExport.IModifiableWorldTree` with `Decals`).
- `ExportCache.ToExportedWorld()` builds the root: `ExportId.SpecialRootDefinition` / `SpecialRootInstance`, root transform = `RevitGeometryParser.WorldTransformation`, `ExportedWorld.set_Geometry`, `new WorldGraphMaterials(ExportCache.Materials)`, `RevitExportedWorld.set_Decals`.

### 1.4 `RevitRenderingSync` / `RevitRenderingSession` / transactions (observed)

- `RevitRenderingSync` (implements Cad.Abstractions `ICadRenderingSync`): `ExportAndStartLiveSyncAsync(ILiveViewport, CancellationToken)`, `RequestStartRendererAsync`, `SetGraphicsOverrideStateAsync`, `PreselectViewForNextStartAsync`, `SetActiveViewIfNot3D` / `GetActiveOrDefault3DViewFromActiveDocument` — uses `UIApplication.ActiveUIDocument`, `UIDocument.ActiveGraphicalView`, `View.ViewType`, `UIDocument.ActiveView`, and posts Revit commands via `RevitCommandId.LookupPostableCommandId` + `UIApplication.PostCommand` (post the default-3D-view command).
- `RevitRenderingSession` (implements `ICadRenderingSession`): session-level ops — `ExportViewportAsync`, `ExportDocumentAsync`, `ExportViewAsync(ICadView)`, `SetReportedViewAsync`, `SetCaptureStateAsync`, `SetLiveCameraEnabledAsync`, `OverwriteMaterialAsync`, `ExecuteAssetOperationsAsync`, `GetAllBimElementsAsync` (→ `BimElementReader.GetBimElements` over `IModifiableWorldTree.RootInstance`).
- **Live updates**: `DocumentObserver` subscribes `ControlledApplication.add_DocumentChanged`, `add_DocumentSynchronizedWithCentral`, `DocumentReloading/ReloadedLatest`; `OnDocumentChanged(DocumentChangedEventArgs)` → `CreateTransaction` using `GetAddedElementIds/GetModifiedElementIds/GetDeletedElementIds` (+`ElementClassFilter` overload), then `HandleDesignOptionChange` (`DesignOption.GetActiveDesignOptionId`, `DesignOptionsDifferenceCalculator`), `HandleSunAndPositionSettings` (re-parse sun, `ISynchronizedLiveViewport.SetSunPosition`), `HandleCurrentView` (→ `NeedsFullExport`), `TriggerTransactionExecution` (queued via `ISynchronizedEventQueue` on the Revit dispatcher); `ExecuteDiffTransaction` → `IRevitTransactionExecutor.ExecuteDiffTransaction` then `IModifiableWorldTree.Set(world)` + `IModifiableMaterialsCollection.Set` + graphics overrides re-application, then `FlatWorldContext`/`FlatWorldHandle` → `ISynchronizedLiveViewport.SetWorld` + `StartExport/FinishExport`.
- **Diff-expansion helpers in `RevitTransactionExecutor`** (all observed): type-change propagation (`FilteredElementCollector.OfClass`, `LogicalOrFilter`), MEP propagation (`ConstructParameterFilter`: `FilterNumericEquals` + `FilterElementIdRule` + `ParameterValueProvider` + `ElementParameterFilter` over modified ids), decal bookkeeping (`WhereIsDecal`, `DecalCache.GetDecalsOfType/GetElementsWithDecal`, `ViewUtils.FilterForElementsBehindDecals`), linked-project checks (`FilteredElementCollector.OfCategory(-2001352)` — see constants §10), worksharing sync → full update, sanitizer ignores view-specific elements, family documents, and categories named `"Lines"`, `"Cameras"`, `"Detail Items"`, `"<Sketch>"`.

---

## 2. Geometry: `GeometryCollector` + `RevitGeometryParser`

**Evidence:** `ev_bbfdedb7…` (38 types, 161 methods).

### 2.1 Tessellation (`RevitGeometryParser`, observed)

- `ParsePolymesh(Autodesk.Revit.DB.PolymeshTopology)` — **Revit already hands Enscape triangulated data**; Enscape reads it directly:
  - `<ParsePolymesh>g__GetTriangleIndices` → `PolymeshTopology.GetFacets()` (triangle index triplets)
  - `g__GetPoints` → `PolymeshTopology.GetPoints()`
  - `g__GetNormals` → `PolymeshTopology.get_DistributionOfNormals`, `GetNormals()`, `GetNormal(i)` (handles per-facet vs per-vertex normal distribution), `NumberOfPoints`
  - `g__GetUVs` → `PolymeshTopology.get_NumberOfUVs`, `GetUVs()`
  - Output: `ExportedFace { TriangleIndices, Points, Normals, UvsFront, IsVisible, IsLayerVisible }` (`GlmSharp.dvec3`/`dvec2` arrays).
- No manual face triangulation of `Solid`/`Face` objects is done by Enscape — **`CustomExporter` performs the tessellation** (`PolymeshTopology`). (Observed: zero calls to `Face.Triangulate`/`Solid.Faces` anywhere in the 11 697-edge plugin-wide edge set.)
- **Instance transforms**: `TransformExtensions.ToGlm(Autodesk.Revit.DB.Transform)` builds `GlmSharp.dmat4` from `Transform.BasisX/BasisY/BasisZ/Origin`; `XYZExtensions.ToGlm` → `dvec3`; `BoundingBoxXYZExtensions` (eight vertices, `Transform.OfPoint` outlines, `Outline` ctor).
- **World transformation**: `RevitGeometryParser` owns `WorldRotation : dmat3`, `WorldTransformation : dmat4`, `InverseWorldTransformation : dmat4` — a fixed axis-system conversion built in the static ctor from GLM unit vectors (Z-up → renderer convention) — *inference: it is a coordinate-frame remap, not project-north rotation*; true-north rotation is applied separately via `GeoLocation.NorthAngle` (§4) and `GetBimTrackTransformation` (uses `ProjectLocation.GetProjectPosition(XYZ.Zero)` → `Angle`, `EastWest`, `NorthSouth`, `Elevation` + `FootToMeter` + `dmat4.RotateY`).
- **Linked documents**: handled by `IExportContext.OnLinkBegin` (`LinkNode.GetDocument`) — geometry inside a link is collected into a child `GeometryCollector` with the link's transform; ids are namespaced by `ElementIdInLinkedProject` and the string `"RevitLinkInstance_"` (seen in plugin strings, used in tree-path naming; exact use site not pinned — noted as minor unknown).

### 2.2 `GeometryCollector` (observed)

Fields `_faces : List<ExportedFace>`, `_instances : List<ExportedModelInstance>`; methods `Add(ExportedModelInstance)`, `Add(ExportedFace)`, `HasGeometry`, `ToModelDefinition()` → `ExportedModelDefinition { Content = ExportedGeometry { Faces }, SubInstances }` (`FillModelDefinition`, `ExportedGeometry.set_Faces`).

### 2.3 Version-compat (`RevitMultiVersion`, observed, `ev_9fda1b83`)

Wraps APIs that changed across Revit versions: `ExportView(CustomExporter, UIApplication, View3D, ctx, logger)` → `CustomExporter.Export(view)`; `GetSymbolGeometryIdentifier(InstanceNode)`; `IsPerspective(CameraInfo)`; `GetSurface{Fore,Back}groundPattern{Visible,Id,Color}` getters/setters on `OverrideGraphicSettings` (2020+ vs older); `GetCategoryHidden/SetCategoryHidden`, `GetLinkVisibilityView` (`View.GetLinkOverrides`, `RevitLinkGraphicsSettings`, `RevitLinkType.get_IsNestedLink`, `RevitLinkInstance.GetLinkDocument`), `CreateImageView`, `AddParameter(FamilyManager…)`, `SubscribeToThemeChanged(UIControlledApplication)`, `SubscribeDocumentReloading/ReloadedLatest` (Application vs ControlledApplication), `GetValue(ElementId)` (version-keyed category dictionary), `ParseElementId(Visual.AssetProperty)`.

### 2.4 Section boxes / cut geometry (`RenderingSync.ViewUtils`, observed)

- `GetAll3DViews/GetAllViews/IsValidEnscapeView`, `GetCutElements(View3D, Document, Transform)` with `GetCutFilter(BoundingBoxXYZ, Transform)` — builds a solid filter for the view's section box (uses `CreateSolidNearBoundingBoxFaces` iterator + `DB.Plane.CreateByNormalAndOrigin`, `PlanarFace.get_FaceNormal` via RevitMultiVersion); logs `"Section box is not axis aligned in coordinates of project: BBox(…)"`.
- Used by `RevitTransactionExecutor.ElementsBehindDecals` to find decal-hosting surfaces.

---

## 3. Lights: `RenderingSync.Lights.LightParser`

**Evidence:** `ev_8f8b35bc…`.

**How lights are found:** Not via `FilteredElementCollector` on light categories — lights arrive through the CustomExporter traversal: `ExportContext.OnLight(Autodesk.Revit.DB.ContentNode)` → `LightParser.ParseLight(ContentNode)` → **`ContentNode.GetAsset()`** → `ParseGenericPhotometricLightAsset(Asset)` reading the Revit **"Generic Photometric Light" rendering asset** with `Autodesk.Revit.DB.Visual.AssetProperties.FindByName(...)`. (Observed: no `OST_LightFixtures` string anywhere in the assembly's 587 literals.)

**Asset properties read (exact `ldstr` literals, observed):**
- Shape: `"lightobjectareatype"` (enum `LightObjectAreaType` = `Point | Rectangle | Disc`), `"rectangle_length"`, `"rectangle_width"`, `"disc_radius"`, `"disc_normal"`, `"isLinearLight"` → `ExportedComplexLight.LightShape`, `ShapeParam1`, `ShapeParam2`
- Distribution: `"distribution"` (enum `Distribution` = `Spherical | Spot | Hemispherical | Ies`), `"fallOff"`, `"hotSpot"`, **`"lightProfileFileName"` (IES path)**, **`"lightProfileCacheData"` (IES file content as byte array)** → `ExportedComplexLight.LightDistribution`, `BeamAngle` (spot; `Enscape.Calculator.SteradiantOfAngle` used for illuminance conversion), `IesFile`, `IesFileContent`
- Color: `"lightTemperature"` (Kelvin → RGB via `Materials.Colors.ColorUtils.GetRgbFromTemperature`), `"filterColor"` (dvec3, via `AssetPropertyExtensionMethods.ParseDvec3`) → `ExportedLight.Color`
- Intensity: `"intensityUnits"` (enum `IntensityUnits` = `LuminousIntensity | LuminousFlux | Illuminance | Wattage`), `"intensityValue"`, `"illuminanceAt"`, `"electricalEfficiency"` → `ExportedLight.Intensity`
- Units: lengths converted with `Enscape.UnitConversion.LengthUnitConverter.FootToMeter`; a static `TransformationToCommonCoordinateSystem : dmat4` re-orients light axes.
- Parsing helpers: `AssetPropertyExtensionMethods.ParseEnum/ParseDouble/ParseBoolean/ParseDvec3/ParseString/ParseByteArray` (typed wrappers over `AssetProperty` values; `AssetPropertyDouble` etc.), `ParseDistance`/`ConvertDistanceToFeet` over `AssetPropertyDistance` ("Converting UnitType (…) to Feet").

**Output model** (`Enscape.Cad.Abstractions` + `Enscape.Cad.ExportedWorld`, observed type catalog `ev_84e764ac…`): `ExportedLight {Color, Intensity}`; `ExportedComplexLight : ExportedLight {LightShape, ShapeParam1/2, LightDistribution, BeamAngle, IesFile, IesFileContent}`; specialized `ExportedPointLight`, `ExportedSpotLight`, `ExportedLinearLight`, `ExportedRectangularLight`, `ExportedDiskLight`, `ExportedIesLight`; plus `Enscape.Cad.CustomObjects.{Light, SphericalLight, SpotLight, LinearLight, RectangularLight, DiskLight, IesLight}` (Enscape's own light objects, for the asset-library/custom-object path, not the Revit-fixture export path).

**IES/texture path resolution** (`RenderingSync.FileLocator`, observed strings + state machines `GetRevitIesProfileDirectories` / `GetRevitTexturesDirectories`): search dirs from Revit.ini `[Directories] AdditionalRenderAppearancePaths`, the document's `ProjectPath`, plus `"Autodesk Shared"`, `"Materials"`, `"Textures"`, `"Sounds"`, `"IES"`, `"Mats"` folders; Revit.ini located via registry `"Autodesk\Revit"`, `"Revit.ini"` (GUID `d2bf4759-52b7-4d10-a0dc-e6b8a2f250b3`).

---

## 4. Sun & location: `RenderingSync.SunAndLocationParser`

**Evidence:** `ev_c28c2c88…`.

- `SunAndLocationParser.Parse(SunAndShadowSettings)` produces either
  - `Enscape.Cad.Views.PlaceAndTimeSunPosition { GeoLocation, LocalTime }` — **place-and-time mode**: geo from `SunAndShadowSettings.get_Latitude/get_Longitude` (the sun's "place"), `LocalTime` from `SiteLocation.get_TimeZone` / `SunAndShadowSettings.get_ActiveFrameTime` (date/time), `get_UsesDST`; or
  - `Enscape.Cad.Views.ExplicitSunPosition { Azimuth, Altitude }` — **static-sun mode**: when `SunAndShadowSettings.get_SunAndShadowType` indicates a statically placed sun; values from `get_Azimuth`/`get_Altitude`.
- `GetSunDirection(SunAndShadowSettings, float)` computes the sun vector: `XYZ.BasisY/BasisX/BasisZ`, `Transform.CreateRotation`, `Transform.OfVector`, `Altitude`, `Azimuth`.
- `ModelLocationParser.Parse(SiteLocation)` → `Enscape.Cad.GeoLocation` (Cad.Abstractions struct): `SiteLocation.get_Latitude/get_Longitude`, plus **true-north rotation**: `Document.get_ActiveProjectLocation` → `ProjectLocation.GetProjectPosition(XYZ.Zero)` → `ProjectPosition.get_Angle` → `GeoLocation.set_NorthAngle` (literal `"Error reading project position"` on failure).
- `ModelLocationParser.GetModelElevation(Document)` → `FilteredElementCollector(doc).OfClass(Autodesk.Revit.DB.Level)` (ldtoken TypeRef `0x01000125` resolved to `DB.Level`) → level elevation → `FootToMeter` → `Enscape.Cad.SiteLocations.SiteLocation.set_Elevation`.
- `RevitSiteLocations.GetSiteLocationAsync/GetSiteLocation` (implements `ICadSiteLocations`): `RevitDocument.VerifyAccess`, `Document.get_ActiveProjectLocation` → `ProjectLocation.GetSiteLocation` → `ModelLocationParser.Parse` + `GetModelElevation`.
- Wiring: `RevitView.GetSunPositionAsync` → `View.get_SunAndShadowSettings` → `SunAndLocationParser.Parse` (observed edge). `DocumentObserver.HandleSunAndPositionSettings` re-parses on document changes and pushes `ISynchronizedLiveViewport.SetSunPosition`.

---

## 5. Materials: `MaterialParser` + `MaterialCombiner` + `AssetFinder` (+ storage/write-back)

**Evidence:** `ev_b5f49af0…` (MaterialParser, 65 types), `ev_35335cb4…` (combiner/finder/keyword-typifier/texture helpers), `ev_e7e292a3…` (MaterialEditor/storage/write-back).

### 5.1 Where material data comes from (observed)

- `MaterialSourceSelector.GetMaterialForWorld(DB.Material, DB.MaterialNode)` → `WorldGraphMaterial { MainMaterial, AlternativeMaterial }`:
  1. `MaterialParser.ParseGraphics(Material)` — **shading/graphics properties**: `Material.get_Color`, `Material.get_Transparency` → `AlphaTransparencySettings { Opacity, RefractiveIndex, TintColor }`, `ColorAlbedoSettings`; material `Name`.
  2. `ReadAppearance`: `IMaterialStorage.Read(Material)` (persistent Enscape override, §5.4) → if it `HasPrecedence`, use it as `enscapeMaterial`; else `MaterialParser.ParseAppearance(Material, MaterialNode)` → `AssetFinder.FindAsset` → `ParseSchema` → `IMaterialCombiner.CombineMaterials(ref, Enscape material)` merging both.
  3. `ExportContext.OnMaterial` calls this per `MaterialNode.MaterialId` (+`MaterialNode.get_HasOverriddenAppearance` / `GetAppearanceOverride` for view-specific appearance overrides and decals).

### 5.2 Appearance assets (observed)

- **`AssetFinder.FindAsset(DB.Material, DB.MaterialNode)`**: `Material.get_AppearanceAssetId` → `Document.GetElement` → **`Autodesk.Revit.DB.AppearanceAssetElement.GetRenderingAsset()`**; also `MaterialNode.GetAppearance()` (per-view override asset); compares `"VersionGUID"` asset properties to detect a *different* asset in ExportContext (`AssetProperties.get_Size`, `FindByName("VersionGUID")`, `AssetPropertyString.get_Value`).
- **Schema dispatch** (`MaterialParser.ParseSchema(Document, Asset)`): reads `Asset`'s base schema via `AssetProperties.FindByName("BaseSchema")` / `"UIDefinition"` / `"Schema"` (literals), then dispatches on **19 Revit appearance schemas** — schema→color-property map built in `.ctor` (literals observed): `CeramicSchema`(ceramic_color), `ConcreteSchema`(concrete_color), `GenericSchema`(generic_diffuse), `GlazingSchema`(glazing_transmittance_color), `HardwoodSchema`(hardwood_color), `MasonryCMUSchema`(masonrycmu_color), `MetallicPaintSchema`(metallicpaint_base_color), `MetalSchema`(metal_type), `MirrorSchema`(mirror_tintcolor), `PlasticVinylSchema`(plasticvinyl_color), `PrismGlazingSchema`(glazing_transmission_color), `PrismLayeredSchema`(layered_diffuse), `PrismMetalSchema`(metal_f0), `PrismOpaqueSchema`(opaque_albedo), `PrismTransparentSchema`(transparent_color), `SolidGlassSchema`(solidglass_transmittance), `StoneSchema`(stone_color), `WallPaintSchema`(wallpaint_color), `WaterSchema`(water_type). (Prism* are Revit's PrismMateriaal/PBR-family schemas.)
- **Per-schema properties read** (exact literal names, all observed; each `ParseXxxSchema` also reads the shared texture block):
  - Generic: `generic_diffuse`, `generic_diffuse_image_fade`, `generic_glossiness`, `generic_is_metal`, `generic_reflectivity_at_0deg`, `generic_reflectivity_at_90deg`, `generic_transparency`, `generic_transparency_image_fade`, `generic_refraction_translucency_weight`, `generic_refraction_index`, `generic_cutout_opacity`, `generic_self_illum_luminance`, `generic_self_illum_filter_map`, `generic_self_illum_color_temperature`, `generic_bump_map`, `generic_bump_amount` (+ `common_Tint_toggle`, `common_Tint_color` via `ParseTint`)
  - Ceramic: `ceramic_type/application/bump/bump_map/bump_amount/pattern/pattern_map/pattern_amount/color`
  - Concrete: `concrete_color/sealant/finish/bump_map/bump_amount/brightmode/bm_map`
  - Glazing: `glazing_transmittance_color/transmittance_map/reflectance/no_levels`; SolidGlass: `solidglass_transmittance/transmittance_custom_color/reflectance/refr_ior/glossiness/bump_enable/bump_map/bump_amount`
  - Hardwood: `hardwood_color/tint_enabled/tint_color/finish/application/imperfections/imperfections_shader/imperfections_amount`
  - MasonryCMU: `masonrycmu_type/color/application/pattern/pattern_map/pattern_height`
  - Metal: `metal_type/color/patina/finish/pattern/pattern_shader/pattern_height/pattern_scale/perforations/perforations_size/perforations_center/perforations_shader`
  - MetallicPaint: `metallicpaint_base_color/base_highlightspread/flecks/flecks_color/flecks_size/pearl/pearl_color/pearl_ior/pearl_amount/topcoat/topcoat_glossy/topcoat_falloff/finish/finish_peelamount`
  - Mirror: `mirror_tintcolor`; PlasticVinyl: `plasticvinyl_type/color/application/bump/bump_map/bump_amount/pattern/pattern_map/pattern_amount`
  - Stone: `stone_color/application/bump/bump_map/bump_amount/pattern/pattern_map/pattern_amount`; WallPaint: `wallpaint_color/finish/application`
  - Water: `water_type/tint_enable/tint_color/bump_amount`
  - PrismOpaque: `opaque_albedo/opaque_f0/opaque_translucency/opaque_mfp/opaque_mfp_modifier/opaque_emission/opaque_luminance/opaque_luminance_modifier/surface_roughness/surface_normal/surface_cutout`; PrismTransparent: `transparent_color/transparent_distance/transparent_ior/surface_roughness/surface_normal/surface_cutout`; PrismMetal: `metal_f0/surface_roughness/...`; PrismGlazing: `surface_albedo/surface_roughness/glazing_transmission_color/glazing_transmission_roughness/glazing_f0/...`; PrismLayered: `layered_diffuse/layered_bottom_f0/layered_roughness/layered_fraction/layered_normal/layered_f0/...`
  - Nested enums encode Revit's enum values: `CeramicType/CeramicApplication/CeramicBump`, `ConcreteSealant/ConcreteFinish/ConcreteWeathering`, `BumpMapType`, `GlazingTransmittanceColor`, `HardwoodFinish/HardwoodApplication/HardwoodReliefPattern`, `MasonryType/MasonryFinish`, `MetalType/MetalFinish/MetalPattern/MetalPerforations`, `MetallicPaintPearl/TopCoat/Finish`, `PlasticVinylType/PlasticVinylFinish`, `SolidGlassReliefPattern`, `StoneFinish/StoneBumpType`, `WallPaintFinish/WallPaintApplication`, `WaterColor/WaterType`, plus heuristic mappers `RoughnessOf(...)`, `ColorOf(MetalType/WaterColor/GlazingTransmittanceColor)`, `OpacityOf/SpecularityOf(PlasticVinylType)`, `CalculateSpecular`, `GetOpacityFromTransparency/GetTransparencyFromOpacity`.
- **Textures** (`ParseTexture(Document, AssetProperty)`): reads `unifiedbitmap_Bitmap` + connected properties (`AssetProperty.GetAllConnectedProperties`, `FindByName`, `AssetPropertyString.get_Value`), `unifiedbitmap_Invert`, `unifiedbitmap_RGBAmount` (brightness), scale/offset/rotation `texture_RealWorldScaleX/Y` (alias `unifiedbitmap_RealWorldScaleX/Y`), `texture_RealWorldOffsetX/Y`, `texture_WAngle` (alias `unifiedbitmap_WAngle`); distances → feet → meters (`ParseDistance`, `FootToMeter`), angle → radians (`DegreeToRadian`), builds `GlmSharp.dmat3` UV transform (`TextureTransformationHelper.GetTransformationDouble`) → `ImageTexture { FilepathTextureSource{Filepath}, Brightness, IsInverted, Slot, Transformation }`. Path resolution via `IFileLocator.Search` (Revit.ini `AdditionalRenderAppearancePaths`, document dir). Bump: `bumpmap_Bitmap`, `bumpmap_Type` (`BumpHeightSettings`/`DisplacementHeightSettings`/`NormalHeightSettings`), `bumpmap_Depth` (`ParseBumpAmount`), `bumpmap_NormalScale` (`ParseNormalIntensity`).
- **Video textures** (observed): `VideoTextureExtractor.TryExtractVideoTexture(DB.Material, ref)` — parses a tag embedded in the **material name** with regex `"\[video:([^\]]*)\]"` and `"|"`-separated params (`TryExtractVideoTextureParams(string, (string,double,double,double)&)`) → `Enscape.Cad.Materials.VideoTexture { VideoSource, ThumbnailSource }`; `PostProcessAppearance` swaps the albedo texture for the video texture (`CreateWithVideoTexture` local function). Thumbnail via `GetThumbnail(VideoTexture)`. Video file extensions (Cad.Base literals): `.mp4 .mpg .m2v .3gp .avi .mov .mkv`.
- **Decals** (`ParseDecals(Asset, Document)` / `ParseDecal`): decal data lives on the **host face's material override asset** (`MaterialNode.GetAppearanceOverride`), properties `"decal-1"`, `"materialId"`, `"top_left-1"`, `"top_right-1"`, `"bottom_left-1"`, `"bottom_right-1"`, `"surface_material"` → `ParsedDecal { Id, Material, Position }` → `ExportedDecal { Material, Position : DecalPosition {TopLeftUv, TopRightUv, BottomLeftUv, BottomRightUv} }` + `DecalCache` (per-element). A full decal appearance schema is parsed too: `decApp_diffuse`, `decApp_reflectivity_at_0deg/90deg`, `decApp_transparency`, `decApp_refraction_index`, `decApp_glossiness`, `decApp_self_illum_luminance/filter_map/color_temperature`, `decApp_bump_map/bump_amount`, `decApp_cutout_opacity` (`ParseDecalAppearanceSchema`, `RemoveFootMeterConversion`).
- Fallback: `MaterialParser.ParseAppearance` may consult `Application.GetAssets` (Revit asset library) when the material lacks an asset (edge observed).

### 5.3 `MaterialCombiner` + `MaterialKeywordTypifier` (observed)

- `CombineMaterials(ref, Enscape.Cad.Materials.Material)` merges the *Enscape* material (persistent/override, literal key `"enscapeMaterial"`) with the parsed *Revit* material (`"revitMaterial"`): `CombineAlbedo` (colored/texture albedo, image fade, tint), `CombineHeight` (bump/displacement/normal), `CombineReflection` (`BasicReflectionSettings{Roughness,Specular}` ↔ `FullReflectionSettings{Roughness,Specular,Metallic}`), `CombineRoughness` (`PlainRoughnessSettings`/`TextureRoughnessSettings`), `CombineTransparency` (per material family incl. `FoliageMaterial`, `SelfIlluminatedMaterial`, `DefaultMaterial`, `GlassMaterial`; frosted/alpha/mask texture settings), `CombineIllumination`.
- `MaterialKeywordTypifier.ApplyName(Material)` — name-based keyword → material type (grass/carpet etc.): `CreateGrass/CreateCarpet` with `StrawSettings` variants (`MaterialKeywordConstants.get_GrassStrawSettings`, `ShortGrass…`, `TallGrass…`, `WildGrass…`, `Carpet…`, `LongCarpet…`).
- **Where "keywords" live (definitive finding):** `Enscape.Materials.MaterialKeywords` in **Cad.Base** (`ev_0286ed50…`): `GetMaterialFamily(string)` matches the material **name** against literals `acryl, aluminium, aluminum, carpaint, ceramic, chrome, clearcoat, cloth, copper, emissive, fabric, foliage, frosted, glass, glazing, grass, leaf, long carpet, marble, metal, mirror, ocean, polished, plastic, river, short carpet, short grass, solid glass, steel, tall grass, vegetation, water, wild grass` → `MaterialKeyword` enum → `MaterialKeywordFamily {Carpet, Ceramic, Clearcoat, Cloth, Emissive, Foliage, Frosted, Glass, Glazing, Grass, Marble, Metal, Mirror, Plastic, Polished, Water}`. Keyword→preset constants: `get_FrostedGlassReflection/GlassReflection/GlazingReflection/MirrorReflection/MetalReflection/PolishedReflection/CeramicReflection/PlasticReflection/ClothReflection/MarbleReflection/ClearcoatReflection`, `get_WaterMaterial/OceanMaterial/RiverMaterial` + wind helpers (`WaterParamsHelper.ConvertWindIntensityAndDirection`).

### 5.4 How the persistent Enscape material is stored & written back (observed — this replaces what older docs called "EnscapeMaterialKeyword")

- **Storage — `MaterialEditor.MaterialStorage`**: an **Autodesk.Revit.DB.ExtensibleStorage schema** named `"EnscapeMaterial"`, vendor GUID **`8904525B-CCC2-4393-8940-F4ECBEA75520`**, with one string field `"content"`. `EnsureSchemaExists()` = `Schema.Lookup(GUID)` else `new SchemaBuilder(GUID){SetSchemaName("EnscapeMaterial"), AddSimpleField("content", string), Finish()}`. `Read(Material)`: `Material.get_AppearanceAssetId` → `Document.GetElement` (**the entity is attached to the material's `AppearanceAssetElement`**) → `Element.GetEntity(schema)`, `Entity.IsValid`, `Entity.Get<string>("content")` → `RevitMaterialJsonSerializer.Deserialize` → `MaterialWithPrecedence { Material, HasPrecedence }`; then `TexturePathUpdater.UpdateTexturePaths` (fixes stale absolute texture paths; uses `Document.get_PathName`, element name). `Write`: serialize → `Entity.Set("content", json)` → `Element.SetEntity` on the AppearanceAssetElement; warns `"Failed to write material … because its corresponding AppearanceAssetElement is null"`.
- **JSON schema** (`RevitMaterialJsonSerializer` + Cad.Base `MaterialJsonSerializer`, literals observed): top `{"Version", "Material", "HasPrecedence"}`, material `{"Name","Type","TypeV2", "Albedo","Height","Reflection","Transparency","Straw","WindSettingsOverrides","Illumination", "WaterColor","WaveHeight","WaveScale","CausticsIntensity", …}` with polymorphic `Type` tags (`DefaultMaterial, GrassMaterial, WaterMaterial, FoliageMaterial, ClearcoatMaterial, CarpetMaterial, SelfIlluminatedMaterial, GlassMaterial`, albedo/height/reflection/transparency/roughness/texture/transformation/color-source sub-objects). **The keyword itself is not a separate field; typing comes from the material `TypeV2` tag and/or material-name keyword matching.**
- **Write-back to Revit graphics** (`MaterialUpdater`): `Update` wraps edits in **`Autodesk.Revit.DB.Visual.AppearanceAssetEditScope`** (`Start`/`Commit`/`Cancel` on `Material.AppearanceAssetId`) and sets asset properties (`AssignDoubleProperty/AssignColorProperty/AssignImageProperty/…` → `generic_diffuse`, `generic_glossiness`, `generic_transparency`, `generic_refraction_index`, `generic_bump_map/amount`, `generic_is_metal`, `generic_cutout_opacity`, `generic_self_illum_*`, `common_Tint_*`, `UnifiedBitmapSchema` bitmap creation); graphics color via `SafeTransaction` + `Material.set_Color` (`UpdateGraphicsColor`, transaction name literal `"Update Graphics Color"`).
- `NativeMaterialCreator.CreateMaterial` (transaction `"Create new material"`), `ApplyTemplateAsset` loads an appearance template asset named `"EnscapeMaterial"` (error literal `"EnscapeMaterial file not found."`), `GetUniqueAppearanceAssetElementName`.
- `RevitMaterials` (implements `ICadMaterials`): material registry + events; `IsEditable` checks asset `BaseSchema == "GenericSchema"`; reacts to `Application.DocumentChanged` / worksharing sync (`SynchronizeAllMaterials`), material-name sanitization `[\:\{\}\[\]\|;<>?`~]`, default naming `"Enscape Material"`.
- **No literal `"EnscapeMaterialKeyword"` exists in either assembly** (checked against all 587 plugin + 413 Cad.Base `#US` strings). If a shared parameter of that name exists in your workflows, it comes from a different version or an external shared-parameters file — report this as an explicit negative observation.

---

## 6. View graphics overrides: `RenderingSync.GraphicsOverrides.*`

**Evidence:** `ev_78743053…` (45 types).

- **`GraphicsOverrideProcessor.ApplyViewOverridesToWorld(View3D, ExportedWorld)`** — gated by `RenderingState.get_AreGraphicsOverridesEnabled` and `RevitMultiVersion.get_GraphicsOverridesSupported`. It **clones** the exported world (`ExportedModelInstance.<Clone>$`, `ExportedModelDefinition.<Clone>$`) and rewrites faces/materials (caches: `_overriddenDefinitionsCache`, `_overriddenMaterialsCache`, `_filterOverridesCache`).
- **Per-element overrides** (`ProcessInstance`): `Autodesk.Revit.DB.View.GetElementOverrides(elementId)` → `OverrideGraphicSettings`.
- **Filter overrides** (`GraphicsOverrideProvider.GetRelevantFilterOverride`): `View.GetOrderedFilters()`, `View.GetIsFilterEnabled(filterId)`, `View.GetFilterOverrides(filterId)`; membership test via `ParameterFilterElement.GetElementFilter()` → `FilteredElementCollector.WherePasses(...)` / `SelectionFilterElement.GetElementIds()`; `CreateCategoryFilter` uses `ParameterFilterElement.GetCategories()` + `Category.get_SubCategories` → `ElementCategoryFilter` + `LogicalOrFilter`.
- **Category overrides** (`GetRelevantCategoryOverride`): walks `Element.get_Category` → `Category.get_Parent` (subcategories inherit) → `Autodesk.Revit.DB.View.GetCategoryOverrides(categoryId)`; special-cases curtain walls: `FamilyInstance.get_Host`, `Wall.get_StackedWallOwnerId`, category id constant `-2000171` (see §10).
- **Combining** (`GraphicsOverrideCombiner.CombineOverrides(element, filter, category)`) merges three `OverrideGraphicSettings` — surface foreground/background pattern `Visible/Id/Color` (via `RevitMultiVersion` getters/setters listed in §2.3) and `OverrideGraphicSettings.get_Transparency` → `SetSurfaceTransparency`.
- **`GraphicsOverrideParser.CreateMaterialOverride`** → `MaterialOverride { Color : dvec3?, Opacity : double? }` — color from the foreground (else background) pattern color when the pattern is visible and its `ElementId != InvalidElementId` and `Color.get_IsValid`; opacity = `GetOpacityFromTransparency(OverrideGraphicSettings.Transparency)` (i.e. Revit's 0–100 transparency inverted to 0–1 opacity, formula in `MaterialParser.GetOpacityFromTransparency`).
- **Application** (`MaterialOverrideExtensions` + `GraphicsOverrideProcessor.ApplyOverride`): produces `ColorAlbedoSettings` (albedo override) and `AlphaTransparencySettings { Opacity, RefractiveIndex, TintColor }` as the `WorldGraphMaterial.AlternativeMaterial` — i.e. **halftone/surface pattern color+transparency overrides become an "alternative material" the renderer uses when graphics overrides are on**; `UpdateDefinition/UpdateFace` rewrite `ExportedFace.MaterialFront/Back`; tree rewrite via `Enscape.WorldExport.WorldGraphOperations.RewriteWorld`.
- Link overrides: `ProcessLink` → `RevitMultiVersion.GetLinkVisibilityView` (see §2.3) and recursion into the link's sub-views.

---

## 7. Ribbon UI: `UserInterface.RibbonUi` + `RibbonLogic`

**Evidence:** `ev_b7f6665e…` (34 types).

- `RibbonUi.CreateUi()` creates **one Revit ribbon tab** (`UIControlledApplication.CreateRibbonTab`, tab name literal `"Enscape™"`) and **10 panels** (`CreateRibbonPanel` + `RibbonPanel.set_Title`; panel/button name literals observed):
  1. **Control** panel — `Start` (start renderer; `StartRendererExternalCommand`), `PauseLiveUpdates`, `ResumeLiveUpdates` (live-update toggle commands)
  2. **Camera** panel — `SplitButtonData` with `ChooseCameraSync` + `"Camera Sync"` (`ToggleCameraSync`) + `ToggleLiveCamera` (themed toggles via `ThemedToggleButton`)
  3. **Active Document** panel — `"ActiveDocument"` label + `"ActiveView"` `ComboBox` (`ComboBoxData`, `ComboBox.AddItem`, `ComboBoxMemberData`) listing 3D views (`RevitViews`)
  4. **Tools** — `RevitTakeScreenshot`, `AssetLibraryBrowser`, `MaterialLibrary`, `MaterialEditor`
  5. **Manage Uploads** — `ShowUploadsManager`
  6. **Sound** — `PlaceSoundsource` split (`PlaceSoundsourceOnFace`, `PlaceSoundsourceOnActiveWorkplane`)
  7. **Settings** — `General Settings`
  8. **Misc** — `OpenFeedback`, `OpenAbout`, `BuyEnscape`
  9. **User** — `OpenUserArea`
- Buttons: `CreateThemedButtonOnPanel(RibbonPanel, name, text, IconFilename, commandName, ButtonWidth)` → `new PushButtonData(name, text, assemblyPath, commandTypeName)`, `set_AvailabilityClassName("AlwaysVisibleExternalCommandAvailability")`, `RibbonPanel.AddItem`, `SetContextualHelp` (help URL literal `https://enscape3d.com/knowledge-base/?utm_source=product&utm_medium=help`), tooltips/tooltips wrapped by `RibbonTextFormatter`. **Command classes** are `IExternalCommand`s wrapped by `ExternalCommandWrapper<T>` (`EnableOrDisableCameraSyncCommand/…ExternalCommand/StartRendererExternalCommand` all `.ctor` → `ExternalCommandWrapper`1..ctor`).
- Icons: light/dark theme pairs via `UIThemeManager.get_CurrentTheme` + `RevitMultiVersion.SubscribeToThemeChanged` (`UIControlledApplication.add_ThemeChanged`); icon files (Cad.Base literals): `Enscape_Start.png`, `Enscape_Pause.png`, `Live_Update.png`, `Sync_Camera_Start/Pause.png`, `Live_Camera_Start/Pause.png`, `Render_Image.png`, `Asset_Library.png`, `Material_Library.png`, `Material_Editor.png`, `Uploads.png`, `Sound_Source.png`, `General_Settings.png`, `Support.png`, `About.png`, `Store.png`, `Account_Yes/No.png`, `Active_Document.png`, `Enscape_Objects.png` (+ `_Dark` variants).
- `RibbonLogic` wires behavior: `CurrentRenderingStateProvider.add_CurrentStateChanged`, `Application.add_DocumentClosed`, `RevitViews.add_ViewRemoving`, `UIApplication.add_ViewActivated`, `Application.add_DocumentChanged`, `ComboBox.add_DropDownOpened`; updates button visibility/enabled state (`StartButton`/`PauseButton`/`ResumeButton` swap via `RibbonItem.set_Visible`, tooltips from `Enscape.Localization.Revit.*`).
- **How Start works** (observed): `SimpleExternalBaseCommand.Execute(ExternalCommandData, ref message, ElementSet)` → `EnscapeRevitPlugin.get_Instance.get_Container` (DI) → `ServiceProviderExtensions.Resolve` → `ExecuteAsync()` (virtual; each command implements it; must-override literal `"Must override virtual method ExecuteAsync"`) → dispatched via `Enscape.Threading.ICadDispatcher` (`FireAndForget`). `StartRendererExternalCommand` raises `RevitRenderingSync.StartRendererRequested` → `RevitRenderingSync.RequestStartRendererAsync` → `ExportAndStartLiveSyncAsync(ILiveViewport, ct)` → `DocumentObserver.StartAtView` (full export + `SetCurrentViewport` + `SetWorld` + `FinishExport`).

---

## 8. Live camera sync: `Views.RevitCameraSync` + `RevitLiveCamera`

**Evidence:** `ev_6308a133…` (60 types).

- **`CameraExporter.ExportView(View3D)`** — a *second, tiny* CustomExporter pass for camera data: `new CameraExportContext` → `new CustomExporter(document, ctx)` → **`set_IncludeGeometricObjects(false)`** → `CustomExporter.Export(view)` → `ctx.CameraInfo`. `CameraExportContext.OnViewBegin(ViewNode)` → **`ViewNode.GetCameraInfo()`** → `ExportedCameraInfo { ViewId, UpOffset, RightOffset, TargetDistance, NearDistance, FarDistance, HorizontalExtent, VerticalExtent, IsPerspective (CameraInfo.get_IsPerspective via RevitMultiVersion) }`.
- **`RevitCameraSync`** (Revit→renderer sync): subscribes `UIApplication.add_Idling`, `CurrentRenderingStateProvider.add_CurrentStateChanged`, `Application.add_DocumentChanged`. On tick: `UpdateCamera` → `TryGetCurrentViewData` (active `UIDocument`, `UIDocument.GetOpenUIViews()` → `UIView`; `TryGetActiveView3D` via `UIDocument.ActiveGraphicalView` + `View3D.get_IsPerspective`; uses `CameraExporter.ExportView` for perspective FOV numbers) → `RevitCameraUtils.CalculateCamera(ViewData)`:
  - Perspective: `View3D.GetOrientation()` (ViewOrientation3D EyePosition/Forward/Up), apply `RevitGeometryParser.WorldTransformation`, `ExportedCameraInfo.HorizontalExtent/TargetDistance` → **`Enscape.Cad.Views.PerspectiveCamera { Position, Orientation, FieldOfView{Angle, Orientation} }`** (angle = 2·atan(hExtent/2/targetDistance) — *inference from fields used*).
  - Orthographic: from `UIView.GetZoomCorners()` (if open UI view) else `View.get_CropBox` + `View.get_Outline` + `View.get_Scale` → **`OrthogonalCamera { Position, Orientation, OrthoWindow }`**.
  - `CalculateOrientation`: Forward/Up + `dvec3.Cross` → `dmat3` → `dquat.FromMat3`, compensated by `WorldRotation` (`Calculator.RotateQuat`).
  - Changed-detection: `ApproximatelyEquals(camera, camera, CameraTolerance)`.
  - Send: `SendCamera(camera)` → `RenderingState.LiveViewport` → `ISynchronizedEventQueue.Publish` (which drives `LiveViewportExtensions.SetCamera` asynchronously).
- **`RevitLiveCamera`** (renderer→Revit "live camera" object): an actual camera **family instance** in the model: family name literal **`"EnscapeCamera"`** (`IsCameraElement` checks element name; family loaded via `RevitUtils.LoadFamily` if absent — camera family ships with the plugin, `"EnscapeCamera"` + `.rfa`/`.rft`/`"RevitFamilies"` literals); parameters `"Pitch"` (instance parameter, `Element.LookupParameter("Pitch").AsDouble`), yaw from `LocationPoint.get_Rotation` (`Location.Rotate`, `Line.CreateBound`); `SpawnNewCamera` → `FamilySymbol.Activate` + `Document.get_Create().NewFamilyInstance`, `Element.set_CreatedPhaseId`; `EnsureOneCameraInDocument`/`RemoveAllCamerasFromDocument` (SafeTransaction, `Document.Delete`).
- **`RevitLiveCameraObserver`** (renderer→Revit polling): `UIControlledApplication.add_Idling` (+`IdlingEventArgs.SetRaiseWithoutDelay`), `CurrentStateChanged`, `DocumentChanged`; `ReadEnscapeCamera`: if `IsLiveCameraEnabled` → `ILiveViewport.CaptureCurrentViewportAsync` → `ViewportSnapshot.Camera` → `HasCameraTransformationChanged` (position `dvec3.ApproxEqual`, orientation `dquat.Dot` vs `MaxDelta`) → `RevitLiveCamera.SetCameraParams` (position via `LocationPoint.set_Point` after `InverseWorldTransformation`, orientation → `ExtractEulerAngles` → SetYaw/SetPitch). Conversely `RevitCameraSync` (above) pushes Revit view → renderer. The **live camera and the camera sync are two distinct features** (`ToggleLiveCamera` vs `ToggleCameraSync` ribbon toggles).

---

## 9. Enscape.Cad.Base — intermediate data model sent to the renderer

**Evidence:** `ev_0e304495…` (world model), `ev_0286ed50…` (materials/FlatWorld), catalogs `ev_2416c8b0…`, `ev_84e764ac…`.

### 9.1 `Enscape.Cad.ExportedWorld` (the "Revit-flavored" scene graph)

- **`ExportedWorld`** `{ Geometry : ExportedModelInstance (root), Materials : WorldGraphMaterials, VRaySceneNames, ExtraData }`
- **`ExportId`** — stable id type over `object` (`ElementIdInLinkedProject` in the plugin), with sentinels: `Empty`, **`NoMaterial`, `UseParentMaterial`, `SpecialLightsContainer` ("Lights"), `SpecialSkatterContainer` ("Skatter"), `SpecialRootDefinition` ("RootDefinition"), `SpecialRootInstance` ("RootInstance")**.
- **`ExportedModelDefinition`** `{ Id, Content : ExportedModelContent, SubInstances : ExportedModelInstance[] }` (definition/instance graph — instances share definitions; plugin uses `InstanceDefinitionCache` keyed by `SymbolGeometryId` to dedup family symbols).
- **`ExportedModelInstance`** `{ Id, PersistentId : string[] (Revit UniqueId path), GroupId, Definition, Transformation : GlmSharp.dmat4, FrontMaterial, BackMaterial, Layer, IsLayerVisible, IsVisible, IsReadonly, FamilyClass, AssetParametricOptionsData }`.
- **`ExportedModelContent`** is a discriminated union: `ExportedGeometry { Faces : ExportedFace[], BillboardConfiguration }`, `ExportedRpc { Guid, HeightInFeet, BbSize, IgnoreFitAabb }`, `ExportedAsset { Id, Source }`, `ExportedSound`, light types, **`ExportedGeometryFromExternalDocument { FileName, Materials }`** (sketchup-style external references — *inference: used for non-walkable/other-CAD content; usage in Revit path not observed*).
- **`ExportedFace`** `{ Id, Points : dvec3[], Normals : dvec3[], TriangleIndices : int[], UvsFront/UvsBack : dvec2[], MaterialFront, MaterialBack, Layer, IsLayerVisible, IsVisible, Decals : ExportedDecal[] }` — **back-face UVs and back material are first-class** (two-sided walls).
- **`WorldGraphMaterials`** wraps `Dictionary<ExportId, WorldGraphMaterial>`; `WorldGraphMaterial { MainMaterial, AlternativeMaterial }` (alternative = graphics-override material).
- `TreePath { PathFromRootToLeaf : ExportId[] }`.

### 9.2 `Enscape.WorldExport` (mutable tree the plugin keeps between frames)

- **`IModifiableWorldTree`/`ModifiableWorldTree`**: indexes `{ ParentOfFaces, ParentOfInstances, InstancesOfDefinitions, FacesById, DefinitionsById, InstancesById, RootInstance }`; ops `Set(ExportedWorld)`, `Get/TryGetDefinition`, `Get/TryGetInstance`, `Replace`, `Remove`, `GetDefinitionsWithContentInLayer`, `UpdateAll`, `CreateOverlayComponentDefinitions`.
- **`IModifiableMaterialsCollection`/`ModifiableMaterialsCollection`**: `CreateSnapshot`, `AddOrUpdate`, `Clear`, `Remove`, `Set`, `Contains` over all materials.
- **`TreeDifferences`** (added `PathedModelDefinition`s / removed `EntityPath`s; traversal, `Concat` semantics with `CombinePersistentIds/CombineTransformations/OverwriteFrontMaterial/...` local functions) + **`WorldGraphOperations.RewriteWorld`** (used by graphics overrides).
- `EntityPath { InstanceId, Parent }`, `EvaluatedEntityPath { PersistentId, GroupId, Transform, FrontMaterial, BackMaterial, FamilyClass, IsReadonly, AssetParametricOptionsData }`, `PathedModelDefinition { Definition, Path, EvaluatedPath }`, `IComponentDefinitions/SimpleComponentDefinitions` (id→definition map with `DefinitionAdded` event), `SoundConstants { VolumeFallback, DistanceAtFullVolumeFallback, DistanceAtZeroVolumeFallback }`.

### 9.3 `Enscape.Cad.FlatWorld` (what actually crosses to the renderer process)

- `FlatWorldContext` (id remapping: `ExportId→FlatWorldMaterialId`, `ExportedModelDefinition→FlatWorldContentId`, `EntityPath↔FlatWorldContentInstanceId`, `ExportId→FlatWorldAssetGroupId`), `FlatWorldConverterFromExportedWorld.ConvertWorld(...)` (flattens the definition/instance graph into `FlatWorldGeometry/FlatWorldFace` with baked `dmat4` transforms; local functions `AddGeometry/AddInstances`), `FlatWorldConverterToExportedWorld`, `FlatWorldHandle { World, WorldContext; GetWorldSnapshotAsync, GetWorldDeltaFromAsync(baseWorld), PickBestMatchAsync }` → `ISynchronizedLiveViewport.SetWorld` (deltas/snapshots over `Enscape.Cad.Remote`).
- Material data model (`Enscape.Cad.Materials`, Cad.Abstractions): `Material` hierarchy `DefaultMaterial, GrassMaterial(+StrawSettings), WaterMaterial(+WaveHeight/WaveScale/CausticsIntensity), FoliageMaterial, ClearcoatMaterial, CarpetMaterial, SelfIlluminatedMaterial, GlassMaterial`; components: `AlbedoSettings {ColorAlbedoSettings, ColoredTextureAlbedoSettings(+ImageFade), TextureAlbedoSettings}`, `HeightSettings {BumpHeightSettings(+Amount), DisplacementHeightSettings, NormalHeightSettings(+Intensity)}`, `RoughnessSettings {Plain, Texture}`, `ReflectionSettings {Basic(Roughness,Specular), Full(+Metallic)}`, `TransparencySettings {FrostedGlass, TransparencyTexture, Alpha(Opacity,RefractiveIndex,TintColor), MaskTexture}`, `IlluminationSettings{Strength,Color}`, `Texture {ImageTexture, VideoTexture}` + `Transformation {Explicit(dmat3), Implicit}` + `TextureSource {FilepathTextureSource, FileContentTextureSource}` + `ColorSource {ExplicitColor, PerObjectColor}`. Fallback constants (`Enscape.Materials.MaterialConstants`): `OpacityFallback, AlbedoColorFallback, TintColorFallback, EmissiveColorFallback/StrengthFallback, RoughnessFallback, SpecularFallback, ImageFadeFallback, MetallicFallback, IndexOfRefractionFallback, BumpAmountFallback(+BumpAmountScale), NormalMapIntensityFallback, TextureBrightnessFallback, WindSettingsFallback, WindDirectionFallback, FallbackColor` (**numeric values are in the static ctor's `ldc.r8` constants — not extracted; unknown**). Thresholds: `MaterialThresholds.IsTransparancyMask(double)`, `IsColoredAlbedoTexture(double)` (alpha-mask heuristics).
- View/camera model (`Enscape.Cad.Views`): `Camera` (base), `PerspectiveCamera{Position,Orientation,FieldOfView{Angle,Orientation}}`, `OrthogonalCamera{Position,Orientation,OrthoWindow}`, `SunPosition` (→ `ExplicitSunPosition{Azimuth,Altitude}`, `PlaceAndTimeSunPosition{GeoLocation,LocalTime}`), `GlobalSectionPlanes`, `ViewSectionPlanes`, `ViewProperties`, `FieldOfViewOrientation`, `WindowPositionOnVirtualScreen`; rendering-sync side: `ICadRenderingSync/ICadRenderingSession/ILiveViewport`, `ViewportContext`, `ViewportSnapshot{Camera,Document}`, `ViewContent{World,Camera,SunPosition}`, `CaptureState`, `ProjectionState`.
- **The renderer transport itself is `Enscape.Cad.Remote` (not inspected in depth here — flagged as follow-up).**

---

## 10. Important constants (all literals observed unless noted)

| Constant | Where used | Note |
|---|---|---|
| RevitAPI / RevitAPIUI **26.0.4.0** | assembly refs | Revit 2026 |
| `-2001352` | `RevitTransactionExecutor.ExecuteLinkedProjectCheck`, `LinkedProjectsWithModifiedLinkType` → `FilteredElementCollector.OfCategory` | observed integer; *inferred* `BuiltInCategory.OST_RevitLinkInstances` |
| `-1012813` | `FilteredElementCollectorExtensions.WhereIsDecal` → `new ElementId(-1012813)` for `ParameterValueProvider` (decal filter via `FilterNumericGreaterOrEqual` + `FilterIntegerRule`) | observed; *inferred* `BuiltInParameter.DECAL_MATERIAL_ID_PARAM` (or related decal parameter) |
| `-2000120` | `GraphicsOverrideProvider.GetRelevantCategoryOverride` | observed; *inferred* curtain-wall category id |
| `-2000171` | `GraphicsOverrideProvider.IsCurtainWallElement` | observed; *inferred* curtain-wall sub-category id |
| `"BIM Track Clash Guid"` | `ExportContext.IsIgnored` | shared/project parameter name |
| `"EnscapeSoundsource"`, `"Volume"`, `"Full Volume Distance"`, `"Zero Volume Distance"`, `"Soundfile"` | sound sources | parameters of the Enscape sound-source family |
| `"EnscapeCamera"`, `"Pitch"`, `"Existing"`, `"__ENSCAPE_TEMPLATE"`, `"{3D}"` | live camera / view creation | family/parameter names |
| `"EnscapeMaterial"` + GUID **`8904525B-CCC2-4393-8940-F4ECBEA75520`** + field `"content"` | `MaterialStorage` extensible-storage schema (attached to the `AppearanceAssetElement`) | persistent Enscape material |
| Extensible-storage schemas for embedded data (GUIDs observed): `EnscapeEmbeddedEnscapeViewsSchema` `9047E550-F89C-4FA0-B2CE-C30BE82A0576` (+Scoped `2C0631A4-CA32-4362-9809-CCCA989F9CF2`, dict `EnscapeEmbeddedEnscapeViewsDict`, storage name `Enscape.EmbeddedEnscapeViews`), `EnscapeEmbeddedSettingsSchema` `4853E9ED-AA4E-43EF-8373-A606789981C5` (Scoped `AF71BC50-0E07-4356-8E9F-B763942B5DA8`), `EnscapeEmbeddedCollaborationDataSchema` `F035F8CD-0CC1-413B-8F0F-FB80E15B0F82` (Scoped `F7B51ED0-6744-4A1B-8ACA-3070503F508C`), `EnscapeEmbeddedSiteModelConfigurationSchema` `BEEB8BC8-F4EA-446F-8E5D-5E246998A8E6` (Scoped `9B643C33-7374-459B-B2D3-ADBD2BE5E07D`), `EnscapeEmbeddedSiteModelDataSchema` `F3CC062C-C61F-46CA-BA50-892963DEA27F` (Scoped `3E8FD166-11A0-4982-9CD4-EBBCAF0068D5`), `EnscapeEmbeddedBuildingPerformanceSchema` `FEF5FD0B-24CD-4231-ADF1-9CB1D4CEE324` (Scoped `9B643C33…` pattern; see literal list) | `EmbeddedData.*` | Enscape view/settings/collab/site data persisted in the RVT |
| `"@⌘Enscape.ValueChunkGuids⌘@"`, `"Id"`, `"Scope"`, `"Data"` | `EmbeddedData.ChunkedDictionary` | chunked extensible storage |
| Asset shared parameters: `"EnscapeAssetId"`, `"EnscapeAssetSource"` (`"REMOTE"`/`"LOCAL"`), `"EnscapeNativeAssetHeight"`, `"EnscapeHeight"`, `"EnscapeParametricOptions"`, `"Enscape AssetDefinition"`, `"assetSourceType"` | Enscape asset families | |
| `"AdditionalRenderAppearancePaths"`, `"ProjectPath"`, `"Directories"`, `"Autodesk\Revit"`, `"Revit.ini"`, GUID `d2bf4759-52b7-4d10-a0dc-e6b8a2f250b3` | `RevitIniObserver`/`FileLocator` | texture/IES search paths |
| Sanitizer category names: `"Lines"`, `"Cameras"`, `"Detail Items"`, `"<Sketch>"` | `RevitTransactionSanitizer.IsIgnoredElement` | |
| Design-option strings: `"Switching from Main Model to primary design option '…'"` etc. | `DesignOptionsDifferenceCalculator` | |
| Video texture tag regex: `"\[video:([^\]]*)\]"` | `VideoTextureExtractor` | |
| Keyword list (§5.3), fallback material name `"Fallback Material"` | Cad.Base | |
| **`"EnscapeMaterialKeyword"` — NOT PRESENT** in either assembly's string tables | — | explicit negative finding |

---

## 11. What a clean-room re-implementation must extract from Revit (checklist)

**A. Geometry pipeline (CustomExporter-based)**
- [ ] Implement `Autodesk.Revit.DB.IExportContext`; run it via `CustomExporter(document, context)`, `ShouldStopOnError=false`, `Export(view3D)` (wrap in a version-compat helper).
- [ ] Handle callbacks in order: `OnViewBegin` (store `ViewNode.ViewId`; optionally `GetCameraInfo()` for FOV), `OnElementBegin/End`, `OnInstanceBegin/End` (use `InstanceNode.GetSymbolGeometryId()` to dedup family-symbol geometry into shared definitions), `OnLinkBegin/End` (`LinkNode.GetDocument` — recurse; namespace ids as (linkId, elementId) pairs), `OnFaceBegin`, `OnPolymesh` (`PolymeshTopology.GetFacets/GetPoints/GetNormals/GetUVs` + `DistributionOfNormals`; two-sided faces → front/back materials), `OnMaterial` (`MaterialNode.MaterialId`, `HasOverriddenAppearance`, `GetAppearanceOverride` for decals), `OnLight` (`ContentNode.GetAsset` photometric parse, §C), `OnRPC` (entourage `ContentNode.GetAsset` → `AssetProperty "Height"/"RPCPlantHeight"`).
- [ ] Maintain per-element geometry collectors → `ModelDefinition {Faces[]}` + `ModelInstance {transform, persistentId (UniqueId), familyClass}`; instance transform from `GroupNode.GetTransform()`; family hand/facing mirror compensation.
- [ ] Fixed world-transform matrix (Z-up→renderer, feet→meters) — camera, sun direction, and exported transforms must all go through the same transform; true north applied separately from `ProjectLocation.GetProjectPosition(XYZ.Zero).Angle`.
- [ ] Skip elements: `CategoryType` checks, view-specific elements, families named "Lines/Cameras/Detail Items/<Sketch>", `"BIM Track Clash Guid"` parameter.

**B. Change tracking (live updates)**
- [ ] `Application.DocumentChanged` → added/modified/deleted `ElementId` sets; expand type changes (collector by class of instances of modified types), MEP system membership (parameter filters), decal hosts (decal parameter filter `ElementId(-1012813)`-style), design-option switches (`DesignOption.GetActiveDesignOptionId`), link-type changes (category `-2001352`-style), current-view modification → full re-export; worksharing sync / reload-latest → full re-export.
- [ ] Throttle to Revit main thread via an `Idling`-driven dispatcher + event queue; only re-export changed subtrees into a retained world tree, then send snapshot/delta.

**C. Lights**
- [ ] Parse Revit's Generic Photometric light asset from `OnLight`'s `ContentNode.GetAsset()`: area type (point/rectangle/disc + dims), distribution (spherical/spot/hemispherical/IES + hotspot/fall-off), **IES file name + cached IES byte payload**, color temperature → RGB, filter color, intensity with unit enum (cd/lm/lux/W) + illuminance distance + electrical efficiency; convert lengths feet→meters; spot beam angle via solid-angle math.

**D. Sun & location**
- [ ] `View.SunAndShadowSettings`: `SunAndShadowType` (static vs date-time), `Azimuth/Altitude` (explicit sun) or `ActiveFrameTime` + `TimeZone` + `UsesDST` (place & time); lat/long from `SunAndShadowSettings` or `Document.ActiveProjectLocation.GetSiteLocation()`; true north from `GetProjectPosition(XYZ.Zero).Angle`; model elevation from lowest/collected `Level` (feet→meters).

**E. Materials**
- [ ] Graphics fallback: `Material.Color`, `Material.Transparency` (transparency% → opacity), name.
- [ ] Appearance: `Material.AppearanceAssetId` → `AppearanceAssetElement.GetRenderingAsset()` (or `MaterialNode.GetAppearance()` for view overrides); dispatch on asset `BaseSchema` for the 19 schemas listed in §5.2 with the exact property names; parse unified bitmaps (`unifiedbitmap_*`, `texture_RealWorld*`, `texture_WAngle`) into image textures with explicit UV transform matrices; bump types (`bumpmap_Type/Depth/NormalScale`); self-illumination; tint (`common_Tint_*`); decal appearance schema (`decApp_*`) with per-corner UVs.
- [ ] Texture path resolution: Revit.ini `[Directories] AdditionalRenderAppearancePaths`, document directory, Revit material-library folders.
- [ ] Video textures: parse `[video:path|…]` tags in material names.
- [ ] Keyword typing: match material **name** against the keyword list (§5.3) → material family presets (grass/carpet straw settings, water/ocean/river, metal/chrome/polished reflection presets).
- [ ] Persistence (optional, for round-tripping): extensible-storage schema on the material's `AppearanceAssetElement` (name + vendor GUID + string field holding a JSON material; schema in §5.4), and write-back via `AppearanceAssetEditScope` setting `generic_*` properties.

**F. Graphics overrides (per-view)**
- [ ] `View.GetElementOverrides(id)` ⊕ `View.GetCategoryOverrides(categoryId)` (walk `Category.Parent`) ⊕ enabled `View.GetOrderedFilters()/GetFilterOverrides()` membership (via `ParameterFilterElement.GetElementFilter()` / `SelectionFilterElement.GetElementIds()`); merge surface pattern fg/bg visibility+id+color and surface transparency; produce an alternative material (albedo color + alpha-transparency) per face when overrides are enabled; link overrides via `View.GetLinkOverrides`.

**G. Views & cameras**
- [ ] View listing (3D views for the combo; `ViewType` checks), section-box cut elements (axis-aligned check; solid filter near box faces).
- [ ] Camera: `ViewNode.GetCameraInfo()` mini-export (`IncludeGeometricObjects=false`) for extents/target/near/far/offsets; perspective from `View3D.GetOrientation()` (+ FOV from extents), orthographic from `UIView.GetZoomCorners()` or `CropBox/Outline/Scale`; only push when changed beyond tolerance; `Idling`-driven both directions.
- [ ] Optional live-camera family instance ("EnscapeCamera": `Pitch` parameter + `LocationPoint.Rotation`), phase-aware creation.

**H. UI**
- [ ] One ribbon tab; panels: Control (Start/Pause/Resume live updates), Camera (camera-sync & live-camera toggles + view combo), Active Document, Tools (screenshot/asset library/material library/material editor), Uploads, Sound placement, Settings, Misc, User; themed icons via `UIThemeManager`; commands as `IExternalCommand` wrappers resolving services from a DI container; availability class always visible.

**I. Data model to hand to a renderer**
- [ ] Definition/instance tree with `dmat4` transforms, per-face front/back materials + back UVs, decals with corner UVs, special ids (root/no-material/use-parent/lights/skatter), materials dictionary with main+alternative materials, world+materials+sun+camera "ViewContent" snapshot per viewport, plus delta computation against a retained tree.

---

## 12. Explicit unknowns & limitations

1. **No CIL decompilation** was performed — method bodies are summarized from decoded signatures, call edges, string anchors, and field lists; exact arithmetic (e.g., `GetOpacityFromTransparency` formula, FOV angle math, world-transform matrix layout) is **not observed**, only the inputs/outputs and helpers involved.
2. **Numeric constants** for `MaterialConstants` fallbacks, `CameraTolerance`/`MaxDelta`, and the exact `BuiltInCategory`/`BuiltInParameter` names behind `-2001352/-1012813/-2000120/-2000171` were not decoded (labels are inference; the integers are observed from `ldc.i4` anchors).
3. **`RevitMultiVersion.GetValue` / `RevitVersionMap`** dictionary contents (per-version category mappings) were not extracted.
4. **`Enscape.Cad.Remote`** (the actual IPC to the renderer process) and **`Enscape.Core`** were not analyzed; `GlmSharpStd` is a third-party math library.
5. `Enscape.Cad.ExportedWorld.DecalPosition` and `BillboardConfiguration` live in **Cad.Abstractions** (field-level detail not extracted).
6. Some signature type operands could not be resolved to names (`TypeRef#NN` for TypeRefs never used as MemberRef parents, e.g. `List<>`/`HashSet<>`/`Func<>`/`CancellationToken` generics); their identities are clear from context but not byte-observed.
7. No execution/runtime tracing was done — all findings are shipped-artifact static observations; dynamic behaviors (event ordering, throttling intervals) are inferred from subscription edges, not measured.
8. `ExportedGeometryFromExternalDocument` and the VRay-scene-name plumbing exist in the model but their Revit-path producers were not observed in this pass.

**No files were created or modified during this analysis.**
</subagent>