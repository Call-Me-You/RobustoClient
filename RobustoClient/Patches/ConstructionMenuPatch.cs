using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client.Construction.UI;
using HarmonyLib;
using Robust.Client.Placement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using RobustoClient.Systems.Construction;
using RobustoClient.Systems.Construction.Blueprints;

namespace RobustoClient.Patches;

[HarmonyPatch(typeof(ConstructionMenu), MethodType.Constructor)]
public static class ConstructionMenuPatch
{
    public static void Postfix(ConstructionMenu __instance)
    {
        try
        {
            AttachBlueprintsPanel(__instance);
        }
        catch (Exception ex)
        {
            MarseyLogger.Fatal($"[ConstructionMenuPatch] Failed to attach Robusto Blueprints UI: {ex}");
        }
    }

    private static void AttachBlueprintsPanel(ConstructionMenu menu)
    {
        // Locate MenuGridViewButton's parent horizontal bar to inject the "Robusto Blueprints" toggle button
        BoxContainer? topRightBar = null;
        try
        {
            var gridBtn = menu.FindControl<Button>("MenuGridViewButton");
            topRightBar = gridBtn.Parent as BoxContainer;
        }
        catch
        {
            // Fallback if control name differs
        }

        var rootBox = menu.Contents.Children.OfType<BoxContainer>().FirstOrDefault();
        if (rootBox == null)
            return;

        var toggleBlueprintsBtn = new Button
        {
            Text = "Robusto Blueprints",
            ToggleMode = true,
            Margin = new Thickness(6, 0, 0, 0)
        };

        if (topRightBar != null)
        {
            topRightBar.AddChild(toggleBlueprintsBtn);
        }
        else
        {
            rootBox.AddChild(toggleBlueprintsBtn);
        }

        // Collapsible right-side column for Robusto Blueprints
        var bpColumn = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            MinWidth = 270,
            Margin = new Thickness(8, 0, 0, 0),
            SeparationOverride = 5,
            Visible = false
        };

        var headerLabel = new Label
        {
            Text = "ROBUSTO BLUEPRINTS",
            FontColorOverride = Color.LimeGreen
        };
        bpColumn.AddChild(headerLabel);

        var nameInput = new LineEdit
        {
            PlaceHolder = "Blueprint name / search...",
            HorizontalExpand = true,
            SelectAllOnFocus = true
        };
        bpColumn.AddChild(nameInput);

        var actionRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 4
        };

        var exportAreaBtn = new Button
        {
            Text = "Save Area (2 Clicks)",
            HorizontalExpand = true
        };
        var refreshBtn = new Button
        {
            Text = "Refresh",
            HorizontalExpand = false
        };
        actionRow.AddChild(exportAreaBtn);
        actionRow.AddChild(refreshBtn);
        bpColumn.AddChild(actionRow);

        // Z-Phase Filter Row (OptionButton + Prev/Next)
        var phaseRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 4
        };

        var prevPhaseBtn = new Button { Text = "<" };
        var phaseSelector = new OptionButton
        {
            HorizontalExpand = true
        };
        var nextPhaseBtn = new Button { Text = ">" };

        phaseSelector.AddItem("All Phases (0 -> 4)", 0);
        phaseSelector.AddItem("0: Subfloor & Lattice", 1);
        phaseSelector.AddItem("1: Cables & Pipes", 2);
        phaseSelector.AddItem("2: Floors & Frames", 3);
        phaseSelector.AddItem("3: Walls & Doors", 4);
        phaseSelector.AddItem("4: Machines & Fixtures", 5);
        phaseSelector.SelectId(0);

        phaseRow.AddChild(prevPhaseBtn);
        phaseRow.AddChild(phaseSelector);
        phaseRow.AddChild(nextPhaseBtn);
        bpColumn.AddChild(phaseRow);

        var autoBuildToggle = new Button
        {
            Text = "Auto-Build Placed Blueprint",
            ToggleMode = true,
            Pressed = true,
            HorizontalExpand = true
        };
        bpColumn.AddChild(autoBuildToggle);

        var stopQueueBtn = new Button
        {
            Text = "Stop Active Build / Clear Queue",
            HorizontalExpand = true
        };
        bpColumn.AddChild(stopQueueBtn);

        var blueprintList = new ItemList
        {
            VerticalExpand = true,
            HorizontalExpand = true,
            SelectMode = ItemList.ItemListSelectMode.Single
        };
        bpColumn.AddChild(blueprintList);

        rootBox.AddChild(bpColumn);

        var displayedBlueprints = new List<RobustaBlueprint>();

        BlueprintZPhase? GetSelectedPhaseFilter()
        {
            return phaseSelector.SelectedId switch
            {
                1 => BlueprintZPhase.Phase0_SubfloorAndLattice,
                2 => BlueprintZPhase.Phase1_UnderfloorUtilities,
                3 => BlueprintZPhase.Phase2_FlooringAndFrames,
                4 => BlueprintZPhase.Phase3_WallsWindowsDoors,
                5 => BlueprintZPhase.Phase4_MachinesAndFixtures,
                _ => null
            };
        }

        void SyncActiveHijackSettings()
        {
            var filter = GetSelectedPhaseFilter();
            bool autoBuild = autoBuildToggle.Pressed;

            if (IoCManager.Resolve<IEntityManager>().EntitySysManager.TryGetEntitySystem<RobustaAutoConstructSystem>(out var autoSys))
            {
                autoSys.ActivePhaseFilter = filter;
                autoSys.AutoBuildPlacedBlueprints = autoBuild;
            }

            if (BlueprintPlacementHijack.ActiveInstance != null)
            {
                BlueprintPlacementHijack.ActiveInstance.ActivePhaseFilter = filter;
                BlueprintPlacementHijack.ActiveInstance.AutoBuildOnPlace = autoBuild;
            }
        }

        void RepopulateBlueprintList()
        {
            blueprintList.Clear();
            displayedBlueprints.Clear();

            var entMan = IoCManager.Resolve<IEntityManager>();
            if (!entMan.EntitySysManager.TryGetEntitySystem<RobustaBlueprintManager>(out var bpManager))
                return;

            var query = nameInput.Text?.Trim() ?? string.Empty;
            foreach (var bp in bpManager.Blueprints)
            {
                if (!string.IsNullOrEmpty(query) &&
                    !bp.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !bp.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                displayedBlueprints.Add(bp);
                blueprintList.AddItem($"{bp.Name} ({bp.Width}x{bp.Height}, {bp.Items.Count} items)");
            }
        }

        void SetPhaseId(int id)
        {
            int wrapped = ((id % 6) + 6) % 6;
            phaseSelector.SelectId(wrapped);
            SyncActiveHijackSettings();
        }

        phaseSelector.OnItemSelected += args =>
        {
            phaseSelector.SelectId(args.Id);
            SyncActiveHijackSettings();
        };

        prevPhaseBtn.OnPressed += _ => SetPhaseId(phaseSelector.SelectedId - 1);
        nextPhaseBtn.OnPressed += _ => SetPhaseId(phaseSelector.SelectedId + 1);

        autoBuildToggle.OnToggled += _ => SyncActiveHijackSettings();

        stopQueueBtn.OnPressed += _ =>
        {
            var entMan = IoCManager.Resolve<IEntityManager>();
            if (entMan.EntitySysManager.TryGetEntitySystem<RobustaAutoConstructSystem>(out var autoSys))
                autoSys.StopJob();
            IoCManager.Resolve<IPlacementManager>().Clear();
        };

        nameInput.OnTextChanged += _ => RepopulateBlueprintList();

        refreshBtn.OnPressed += _ =>
        {
            var entMan = IoCManager.Resolve<IEntityManager>();
            if (entMan.EntitySysManager.TryGetEntitySystem<RobustaBlueprintManager>(out var bpManager))
                bpManager.LoadAllBlueprints();
            RepopulateBlueprintList();
        };

        exportAreaBtn.OnPressed += _ =>
        {
            var entMan = IoCManager.Resolve<IEntityManager>();
            if (entMan.EntitySysManager.TryGetEntitySystem<RobustaBlueprintManager>(out var bpManager))
            {
                bpManager.OnBlueprintsReloaded -= RepopulateBlueprintList;
                bpManager.OnBlueprintsReloaded += RepopulateBlueprintList;

                var bpName = string.IsNullOrWhiteSpace(nameInput.Text)
                    ? $"blueprint_{DateTime.Now:HHmmss}"
                    : nameInput.Text.Trim();
                nameInput.Text = string.Empty;
                bpManager.StartAreaExportSelection(bpName);
            }
        };

        blueprintList.OnItemSelected += args =>
        {
            if (args.ItemIndex < 0 || args.ItemIndex >= displayedBlueprints.Count)
                return;

            var selectedBp = displayedBlueprints[args.ItemIndex];
            var placement = IoCManager.Resolve<IPlacementManager>();
            var hijack = new BlueprintPlacementHijack(
                selectedBp,
                GetSelectedPhaseFilter(),
                autoBuildToggle.Pressed);

            placement.BeginPlacing(new PlacementInformation
            {
                IsTile = false,
                PlacementOption = "SnapgridCenter"
            }, hijack);
        };

        toggleBlueprintsBtn.OnToggled += args =>
        {
            bpColumn.Visible = args.Pressed;
            if (args.Pressed)
            {
                menu.MinSize = new Vector2(840, 320);
                if (menu.Size.X < 840)
                    menu.SetSize = new Vector2(840, Math.Max(450, menu.Size.Y));

                var entMan = IoCManager.Resolve<IEntityManager>();
                if (entMan.EntitySysManager.TryGetEntitySystem<RobustaBlueprintManager>(out var bpManager))
                {
                    bpManager.OnBlueprintsReloaded -= RepopulateBlueprintList;
                    bpManager.OnBlueprintsReloaded += RepopulateBlueprintList;
                }
                RepopulateBlueprintList();
            }
            else
            {
                if (IoCManager.Resolve<IPlacementManager>() is PlacementManager pm &&
                    pm.Hijack is BlueprintPlacementHijack)
                {
                    pm.Clear();
                }
                BlueprintPlacementHijack.ClearActiveInstance();
                menu.MinSize = new Vector2(560, 320);
                menu.SetSize = new Vector2(560, Math.Max(450, menu.Size.Y));
            }
        };
    }
}
