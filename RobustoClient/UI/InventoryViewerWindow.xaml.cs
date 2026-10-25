using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using RobustoClient.Systems;

namespace RobustoClient.UI;

public sealed class InventoryViewerWindow : DefaultWindow
{
    private EntityUid _currentTarget;
    private List<InventoryEntry> _allEntries = new();
    private string _searchQuery = "";
    private int _selectedSortMode = 0; // 0: By Container, 1: By Severity, 2: By Name
    private int _selectedTierFilter = 0; // 0: All, 1: Contraband Only, 2: Syndicate, 3: GrandTheft, 4: Major, 5: Restricted, 6: Minor

    private readonly Label _headerLabel;
    private readonly Label _countLabel;
    private readonly LineEdit _searchBar;
    private readonly Button _refreshButton;
    private readonly OptionButton _sortDropdown;
    private readonly OptionButton _tierFilterDropdown;
    private readonly BoxContainer _itemList;

    public InventoryViewerWindow()
    {
        Title = "Inventory Viewer";
        MinSize = new Vector2(480, 540);
        SetSize = new Vector2(500, 580);

        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Margin = new Thickness(10),
            SeparationOverride = 8,
            HorizontalExpand = true,
            VerticalExpand = true
        };

        // Row 1: Header (Target Name & Item Count)
        var headerRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6
        };

        _headerLabel = new Label
        {
            Text = "Inventory",
            HorizontalExpand = true,
            FontColorOverride = Color.FromHex("#6EBFCF")
        };

        _countLabel = new Label
        {
            Text = "0 items",
            FontColorOverride = Color.FromHex("#AAAAAA")
        };

        headerRow.AddChild(_headerLabel);
        headerRow.AddChild(_countLabel);
        root.AddChild(headerRow);

        // Row 2: Search Bar & Refresh Button
        var searchRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6
        };

        _searchBar = new LineEdit
        {
            PlaceHolder = "Search item, container, prototype, or tier...",
            HorizontalExpand = true
        };

        _refreshButton = new Button
        {
            Text = "Refresh"
        };

        searchRow.AddChild(_searchBar);
        searchRow.AddChild(_refreshButton);
        root.AddChild(searchRow);

        // Row 3: Sort & Filter Toolbars
        var controlRow = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6
        };

        _sortDropdown = new OptionButton { HorizontalExpand = true };
        _sortDropdown.AddItem("Sort: By Container", 0);
        _sortDropdown.AddItem("Sort: By Severity Tier", 1);
        _sortDropdown.AddItem("Sort: By Name (A-Z)", 2);
        _sortDropdown.SelectId(0);

        _tierFilterDropdown = new OptionButton { HorizontalExpand = true };
        _tierFilterDropdown.AddItem("Filter: All Items", 0);
        _tierFilterDropdown.AddItem("Filter: Any Contraband", 1);
        _tierFilterDropdown.AddItem("Filter: Syndicate", 2);
        _tierFilterDropdown.AddItem("Filter: Grand Theft", 3);
        _tierFilterDropdown.AddItem("Filter: Major", 4);
        _tierFilterDropdown.AddItem("Filter: Restricted", 5);
        _tierFilterDropdown.AddItem("Filter: Minor", 6);
        _tierFilterDropdown.SelectId(0);

        controlRow.AddChild(_sortDropdown);
        controlRow.AddChild(_tierFilterDropdown);
        root.AddChild(controlRow);

        // Row 4: Scrollable Item List
        var scrollContainer = new ScrollContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true
        };

        _itemList = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 2
        };

        scrollContainer.AddChild(_itemList);
        root.AddChild(scrollContainer);

        Contents.AddChild(root);

        // --- Event Listeners ---
        _searchBar.OnTextChanged += args =>
        {
            _searchQuery = args.Text.Trim();
            PopulateList();
        };

        _sortDropdown.OnItemSelected += args =>
        {
            _sortDropdown.SelectId(args.Id);
            _selectedSortMode = args.Id;
            PopulateList();
        };

        _tierFilterDropdown.OnItemSelected += args =>
        {
            _tierFilterDropdown.SelectId(args.Id);
            _selectedTierFilter = args.Id;
            PopulateList();
        };

        _refreshButton.OnPressed += _ =>
        {
            var entMan = IoCManager.Resolve<IEntityManager>();

            if (_currentTarget.IsValid() && entMan.EntityExists(_currentTarget))
            {
                var sys = entMan.System<RobustaInventoryViewerSystem>();
                var entries = sys.GetFullInventory(_currentTarget);
                var name = entMan.GetComponent<MetaDataComponent>(_currentTarget).EntityName;

                SetData(_currentTarget, name, entries);
            }
            else
            {
                _headerLabel.Text = "Inventory — Target unavailable";
                _countLabel.Text = "Entity no longer exists.";
                _itemList.RemoveAllChildren();
            }
        };
    }

    public void SetData(EntityUid target, string targetName, List<InventoryEntry> entries)
    {
        _currentTarget = target;
        _allEntries = entries;
        _headerLabel.Text = $"Inventory — {targetName}";

        PopulateList();
    }

    private void PopulateList()
    {
        _itemList.RemoveAllChildren();

        // 1. Filter by Contraband Severity Tier
        var filtered = _allEntries.AsEnumerable();

        filtered = _selectedTierFilter switch
        {
            1 => filtered.Where(e => !string.IsNullOrEmpty(e.ContrabandSeverity)), // Any contraband
            2 => filtered.Where(e => e.ContrabandSeverity.Equals("Syndicate", StringComparison.OrdinalIgnoreCase)),
            3 => filtered.Where(e => e.ContrabandSeverity.Equals("GrandTheft", StringComparison.OrdinalIgnoreCase)),
            4 => filtered.Where(e => e.ContrabandSeverity.Equals("Major", StringComparison.OrdinalIgnoreCase)),
            5 => filtered.Where(e => e.ContrabandSeverity.Equals("Restricted", StringComparison.OrdinalIgnoreCase)),
            6 => filtered.Where(e => e.ContrabandSeverity.Equals("Minor", StringComparison.OrdinalIgnoreCase)),
            _ => filtered // 0: All
        };

        // 2. Filter by Search Query
        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            filtered = filtered.Where(e =>
                e.ItemName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                e.ContainerName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                e.PrototypeId.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                e.ContrabandSeverity.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
        }

        var filteredList = filtered.ToList();

        // Update count label
        _countLabel.Text = _allEntries.Count == filteredList.Count
            ? $"{_allEntries.Count} items"
            : $"{filteredList.Count} / {_allEntries.Count} items";

        if (filteredList.Count == 0)
        {
            var emptyMessage = _allEntries.Count == 0
                ? "No items found (or server did not replicate contents)."
                : "No items match the current filter.";

            _itemList.AddChild(new Label
            {
                Text = emptyMessage,
                FontColorOverride = Color.Gray,
                Margin = new Thickness(0, 10, 0, 0)
            });
            return;
        }

        // 3. Render according to selected Sort Mode
        if (_selectedSortMode == 1)
        {
            // --- SORT BY SEVERITY TIER ---
            // Groups items directly under their contraband severity categories
            var groups = filteredList
                .GroupBy(e => string.IsNullOrEmpty(e.ContrabandSeverity) ? "Legal / Standard Items" : $"{FormatSeverity(e.ContrabandSeverity)} Contraband")
                .OrderByDescending(g => GetGroupSeverityWeight(g.Key));

            foreach (var group in groups)
            {
                var groupColor = GetGroupHeaderColor(group.Key);

                var headerBox = new BoxContainer
                {
                    Orientation = BoxContainer.LayoutOrientation.Horizontal,
                    Margin = new Thickness(0, 8, 0, 2)
                };

                var headerLabel = new Label
                {
                    Text = $"▼ {group.Key.ToUpperInvariant()} ({group.Count()})",
                    FontColorOverride = groupColor,
                    HorizontalExpand = true
                };

                headerBox.AddChild(headerLabel);
                _itemList.AddChild(headerBox);

                foreach (var item in group.OrderBy(i => i.ItemName))
                {
                    var itemRow = new BoxContainer
                    {
                        Orientation = BoxContainer.LayoutOrientation.Horizontal,
                        Margin = new Thickness(12, 1, 0, 1)
                    };

                    var itemColor = GetSeverityColor(item.ContrabandSeverity);
                    var itemLabel = new Label
                    {
                        Text = $"• {item.ItemName} ({item.ContainerName})",
                        FontColorOverride = itemColor,
                        ToolTip = $"Severity: {(string.IsNullOrEmpty(item.ContrabandSeverity) ? "None" : item.ContrabandSeverity)}\nPrototype: {item.PrototypeId}\nUID: {item.Uid}",
                        HorizontalExpand = true
                    };

                    itemRow.AddChild(itemLabel);

                    if (!string.IsNullOrEmpty(item.ContrabandSeverity))
                    {
                        var badge = new Label
                        {
                            Text = $"[{FormatSeverity(item.ContrabandSeverity).ToUpperInvariant()}]",
                            FontColorOverride = itemColor,
                            Margin = new Thickness(6, 0, 0, 0)
                        };
                        itemRow.AddChild(badge);
                    }

                    _itemList.AddChild(itemRow);
                }
            }
        }
        else
        {
            // --- SORT BY CONTAINER (0) OR BY NAME (2) ---
            var groups = filteredList
                .GroupBy(e => e.ContainerName)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                var headerBox = new BoxContainer
                {
                    Orientation = BoxContainer.LayoutOrientation.Horizontal,
                    Margin = new Thickness(0, 8, 0, 2)
                };

                var headerLabel = new Label
                {
                    Text = $"▼ {group.Key}",
                    FontColorOverride = Color.FromHex("#6EBFCF"),
                    HorizontalExpand = true
                };

                headerBox.AddChild(headerLabel);
                _itemList.AddChild(headerBox);

                var sortedItems = _selectedSortMode == 2
                    ? group.OrderBy(i => i.ItemName)
                    : group.OrderByDescending(i => GetSeverityWeight(i.ContrabandSeverity)).ThenBy(i => i.ItemName);

                foreach (var item in sortedItems)
                {
                    var itemRow = new BoxContainer
                    {
                        Orientation = BoxContainer.LayoutOrientation.Horizontal,
                        Margin = new Thickness(12, 1, 0, 1)
                    };

                    var itemColor = GetSeverityColor(item.ContrabandSeverity);
                    var itemLabel = new Label
                    {
                        Text = $"• {item.ItemName}",
                        FontColorOverride = itemColor,
                        ToolTip = $"Severity: {(string.IsNullOrEmpty(item.ContrabandSeverity) ? "None" : item.ContrabandSeverity)}\nPrototype: {item.PrototypeId}\nUID: {item.Uid}",
                        HorizontalExpand = true
                    };

                    itemRow.AddChild(itemLabel);

                    if (!string.IsNullOrEmpty(item.ContrabandSeverity))
                    {
                        var badge = new Label
                        {
                            Text = $"[{FormatSeverity(item.ContrabandSeverity).ToUpperInvariant()}]",
                            FontColorOverride = itemColor,
                            Margin = new Thickness(6, 0, 0, 0)
                        };
                        itemRow.AddChild(badge);
                    }

                    _itemList.AddChild(itemRow);
                }
            }
        }
    }

    private static int GetSeverityWeight(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "syndicate" => 6,
            "grandtheft" => 5,
            "major" => 4,
            "restricted" => 3,
            "minor" => 2,
            "" => 0,
            _ => 1
        };
    }

    private static int GetGroupSeverityWeight(string groupTitle)
    {
        if (groupTitle.StartsWith("Syndicate", StringComparison.OrdinalIgnoreCase)) return 6;
        if (groupTitle.StartsWith("Grand Theft", StringComparison.OrdinalIgnoreCase) || groupTitle.StartsWith("Grandtheft", StringComparison.OrdinalIgnoreCase)) return 5;
        if (groupTitle.StartsWith("Major", StringComparison.OrdinalIgnoreCase)) return 4;
        if (groupTitle.StartsWith("Restricted", StringComparison.OrdinalIgnoreCase)) return 3;
        if (groupTitle.StartsWith("Minor", StringComparison.OrdinalIgnoreCase)) return 2;
        if (groupTitle.StartsWith("Legal", StringComparison.OrdinalIgnoreCase)) return 0;
        return 1;
    }

    private static string FormatSeverity(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "grandtheft" => "Grand Theft",
            "syndicate" => "Syndicate",
            "major" => "Major",
            "restricted" => "Restricted",
            "minor" => "Minor",
            _ => severity
        };
    }

    private static Color GetSeverityColor(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "syndicate" => Color.FromHex("#FF6B6B"),   // Red
            "grandtheft" => Color.FromHex("#FFA500"),  // Orange
            "major" => Color.FromHex("#FFD700"),       // Gold / Yellow
            "restricted" => Color.FromHex("#7090FF"),  // Blue-purple
            "minor" => Color.FromHex("#B0B0B0"),       // Silver-gray
            _ => string.IsNullOrEmpty(severity) ? Color.White : Color.FromHex("#E090FF")
        };
    }

    private static Color GetGroupHeaderColor(string groupTitle)
    {
        if (groupTitle.StartsWith("Syndicate", StringComparison.OrdinalIgnoreCase)) return Color.FromHex("#FF6B6B");
        if (groupTitle.StartsWith("Grand Theft", StringComparison.OrdinalIgnoreCase) || groupTitle.StartsWith("Grandtheft", StringComparison.OrdinalIgnoreCase)) return Color.FromHex("#FFA500");
        if (groupTitle.StartsWith("Major", StringComparison.OrdinalIgnoreCase)) return Color.FromHex("#FFD700");
        if (groupTitle.StartsWith("Restricted", StringComparison.OrdinalIgnoreCase)) return Color.FromHex("#7090FF");
        if (groupTitle.StartsWith("Minor", StringComparison.OrdinalIgnoreCase)) return Color.FromHex("#B0B0B0");
        return Color.FromHex("#6EBFCF");
    }
}