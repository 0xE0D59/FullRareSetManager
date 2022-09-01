using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using FullRareSetManager.SetParts;
using FullRareSetManager.Utilities;
using ImGuiNET;
using SharpDX;

namespace FullRareSetManager
{
    public class FullRareSetManagerCore : BaseSettingsPlugin<FullRareSetManagerSettings>
    {
        private const int INPUT_DELAY = 15;
        private bool _bDropAllItems;
        private Inventory _currentOpenedStashTab;
        private string _currentOpenedStashTabName;
        private CurrentSetInfo _currentSetData;
        private string _drawInfoString = "";
        private DropAllToInventory _inventDrop;
        private BaseSetPart[] _itemSetTypes;
        private StashData _sData;
        public ItemDisplayData[] DisplayData;
        public FRSetManagerPublishInformation FrSetManagerPublishInformation;
        private bool _allowScanTabs = true;
        private Stopwatch _fixStopwatch = new Stopwatch();
        private Coroutine _coroutineWorker;
        private const string CoroutineNameVendor = "FRSM_Vendor";
        private const string CoroutineNameDropToStash = "FRSM_DropToStash";

        public override void ReceiveEvent(string eventId, object args)
        {
            if (!Settings.Enable.Value) return;

            if (eventId == "stashie_start_drop_items")
            {
                _fixStopwatch.Restart();
                _allowScanTabs = false;
            }
            else if (eventId == "stashie_stop_drop_items")
            {
                _allowScanTabs = true;
            }
            else if (eventId == "stashie_finish_drop_items_to_stash_tab")
            {
                _fixStopwatch.Restart();
                UpdateStashes();
                UpdatePlayerInventory();
                UpdateItemsSetsInfo();
            }
        }

        public override bool Initialise()
        {
            Input.RegisterKey(Settings.DropToInventoryKey.Value);
            _sData = StashData.Load(this);

            if (_sData == null)
            {
                LogMessage(
                    "RareSetManager: Can't load cached items from file StashData.json. Creating new config. Open stash tabs for updating info. Tell to developer if this happen often enough.",
                    10);

                _sData = new StashData();
            }

            _inventDrop = new DropAllToInventory(this);

            DisplayData = new ItemDisplayData[8];

            for (var i = 0; i <= 7; i++)
            {
                DisplayData[i] = new ItemDisplayData();
            }

            UpdateItemsSetsInfo();

            Settings.WeaponTypePriority.SetListValues(new List<string> { "Two handed", "One handed" });

            Settings.CalcByFreeSpace.OnValueChanged += delegate { UpdateItemsSetsInfo(); };

            FrSetManagerPublishInformation = new FRSetManagerPublishInformation();
            //WorldItemsController.OnEntityAdded += args => EntityAdded(args.Entity);
            //WorldItemsController.OnEntityRemoved += args => EntityRemoved(args.Entity);
            //WorldItemsController.OnItemPicked += WorldItemsControllerOnOnItemPicked;
            return true;
        }

        public override void EntityAdded(Entity entity)
        {
            if (!Settings.EnableBorders.Value)
                return;

            if (entity.Type != EntityType.WorldItem)
                return;

            if (!Settings.Enable || GameController.Area.CurrentArea.IsTown ||
                _currentAlerts.ContainsKey(entity))
                return;

            var item = entity?.GetComponent<WorldItem>()?.ItemEntity;

            if (item == null) return;

            var visitResult = ProcessItem(item);

            if (visitResult == null) return;

            if (Settings.IgnoreOneHanded && visitResult.ItemType == StashItemType.OneHanded)
                visitResult = null;

            if (visitResult == null) return;

            if (Settings.SmallWeaponOnly && (visitResult.ItemType == StashItemType.OneHanded ||
                                             visitResult.ItemType == StashItemType.TwoHanded)
                                         && (visitResult.Height > 3 ||
                                             visitResult.Width > 1))
                visitResult = null;

            if (visitResult == null) return;

            var index = (int)visitResult.ItemType;

            if (index > 7)
                index = 0;

            var displData = DisplayData[index];

            _currentAlerts.Add(entity, displData);
        }

        public override void EntityRemoved(Entity entity)
        {
            if (!Settings.EnableBorders.Value)
                return;

            if (entity.Type != EntityType.WorldItem)
                return;

            if (Vector2.Distance(entity.GridPos, GameController.Player.GridPos) < 10)
            {
                //item picked by player?
                var wi = entity.GetComponent<WorldItem>();
                var filteredItemResult = ProcessItem(wi.ItemEntity);

                if (filteredItemResult == null)
                    return;
                filteredItemResult.BInPlayerInventory = true;
                _sData.PlayerInventory.StashTabItems.Add(filteredItemResult);
                UpdateItemsSetsInfo();
            }

            _currentAlerts.Remove(entity);
            _currentLabels.Remove(entity.Address);
        }

        public override void AreaChange(AreaInstance area)
        {
            _currentLabels.Clear();
            _currentAlerts.Clear();
        }

        public class ClassForPickit
        {
            public ItemDisplayData[] dataArray { get; set; }
            public int MaxItemSet { get; set; }
        }

        private void MoveMouseToElement(Vector2 pos)
        {
            Input.SetCursorPos(pos + GameController.Window.GetWindowRectangle().TopLeft);
        }

        private IEnumerator Delay(int ms = 0)
        {
            yield return new WaitTime(Settings.ExtraDelay.Value + ms);
        }

        private IEnumerator Click(MouseButtons mouseButton = MouseButtons.Left)
        {
            Input.Click(mouseButton);
            yield return Delay();
        }

        private IEnumerator ClickElement(Vector2 pos, MouseButtons mouseButton = MouseButtons.Left)
        {
            MoveMouseToElement(pos);
            yield return Click(mouseButton);
        }

        public IEnumerator SwitchToTab(int tabIndex)
        {
            // We don't want to Switch to a tab that we are already on or that has the magic number for affinities
            //var stashPanel = GameController.Game.IngameState.IngameUi.StashElement;

            var visibleStashIndex = GetIndexOfCurrentVisibleTab();
            var travelDistance = Math.Abs(tabIndex - visibleStashIndex);
            if (travelDistance == 0) yield break;

            if (travelDistance < 2 || !IsSliderPresent())
                yield return SwitchToTabViaArrowKeys(tabIndex);
            else
                yield return SwitchToTabViaDropdownMenu(tabIndex);

            yield return Delay();
        }

        private bool DropDownMenuIsVisible()
        {
            return GameController.Game.IngameState.IngameUi.StashElement.ViewAllStashPanel.IsVisible;
        }

        private IEnumerator SwitchToTabViaDropdownMenu(int tabIndex)
        {
            if (!DropDownMenuIsVisible())
            {
                yield return OpenDropDownMenu();
            }

            yield return ClickDropDownMenuStashTabLabel(tabIndex);
        }

        private IEnumerator OpenDropDownMenu()
        {
            var button = GameController.Game.IngameState.IngameUi.StashElement.ViewAllStashButton.GetClientRect();
            yield return ClickElement(button.Center);
            while (!DropDownMenuIsVisible())
            {
                yield return Delay(1);
            }
        }

        private IEnumerator ClickDropDownMenuStashTabLabel(int tabIndex)
        {
            var dropdownMenu = GameController.Game.IngameState.IngameUi.StashElement.ViewAllStashPanel;
            var stashTabLabels = dropdownMenu.GetChildAtIndex(1);

            //if the stash tab index we want to visit is less or equal to 30, then we scroll all the way to the top.
            //scroll amount (clicks) should always be (stash_tab_count - 31);
            //TODO(if the guy has more than 31*2 tabs and wants to visit stash tab 32 fx, then we need to scroll all the way up (or down) and then scroll 13 clicks after.)

            var clickable = StashLabelIsClickable(tabIndex);
            // we want to go to stash 32 (index 31).
            // 44 - 31 = 13
            // 31 + 45 - 44 = 30
            // MaxShownSideBarStashTabs + _stashCount - tabIndex = index
            var index = clickable
                ? tabIndex
                : tabIndex - (((int)GameController.Game.IngameState.IngameUi.StashElement.TotalStashes) - 1 - (31 - 1));
            var pos = stashTabLabels.GetChildAtIndex(index).GetClientRect().Center;
            MoveMouseToElement(pos);
            if (IsSliderPresent())
            {
                var clicks = ((int)GameController.Game.IngameState.IngameUi.StashElement.TotalStashes) - 31;
                yield return Delay(3);
                VerticalScroll(scrollUp: clickable, clicks: clicks);
                yield return Delay(3);
            }

            DebugWindow.LogMsg($"[FRSM] Moving to tab '{tabIndex}'.", 3, Color.LightGray);
            yield return Click();
        }

        private static void VerticalScroll(bool scrollUp, int clicks)
        {
            const int wheelDelta = 120;
            if (scrollUp)
                WinApi.mouse_event(Input.MOUSE_EVENT_WHEEL, 0, 0, clicks * wheelDelta, 0);
            else
                WinApi.mouse_event(Input.MOUSE_EVENT_WHEEL, 0, 0, -(clicks * wheelDelta), 0);
        }

        private static bool StashLabelIsClickable(int index)
        {
            return index + 1 < 31;
        }

        private IEnumerator SwitchToTabViaArrowKeys(int tabIndex, int numberOfTries = 1)
        {
            if (numberOfTries >= 3)
            {
                yield break;
            }

            var indexOfCurrentVisibleTab = GetIndexOfCurrentVisibleTab();
            var travelDistance = tabIndex - indexOfCurrentVisibleTab;
            var tabIsToTheLeft = travelDistance < 0;
            travelDistance = Math.Abs(travelDistance);

            DebugWindow.LogMsg($"[FRSM] Moving to tab '{tabIndex}'.", 3, Color.LightGray);

            if (tabIsToTheLeft)
            {
                yield return PressKey(Keys.Left, travelDistance);
            }
            else
            {
                yield return PressKey(Keys.Right, travelDistance);
            }

            if (GetIndexOfCurrentVisibleTab() != tabIndex)
            {
                yield return Delay(25);
                if (GetIndexOfCurrentVisibleTab() != tabIndex)
                    yield return SwitchToTabViaArrowKeys(tabIndex, numberOfTries + 1);
            }
        }

        private IEnumerator PressKey(Keys key, int repetitions = 1)
        {
            for (var i = 0; i < repetitions; i++)
            {
                yield return Input.KeyPress(key);
            }
        }

        private bool IsSliderPresent()
        {
            return ((int)GameController.Game.IngameState.IngameUi.StashElement.TotalStashes) > 31;
        }

        private int GetIndexOfCurrentVisibleTab()
        {
            return GameController.Game.IngameState.IngameUi.StashElement.IndexVisibleStash;
        }

        public class FRSetManagerPublishInformation
        {
            public int GatheredWeapons { get; set; } = 0;
            public int GatheredHelmets { get; set; } = 0;
            public int GatheredBodyArmors { get; set; } = 0;
            public int GatheredGloves { get; set; } = 0;
            public int GatheredBoots { get; set; } = 0;
            public int GatheredBelts { get; set; } = 0;
            public int GatheredAmulets { get; set; } = 0;
            public int GatheredRings { get; set; } = 0;

            public int WantedSets { get; set; } = 0;
        }

        public override void Render()
        {
            if (!GameController.Game.IngameState.InGame) return;

            FrSetManagerPublishInformation.WantedSets = Settings.MaxSets.Value;
            var rareSetData = _itemSetTypes;
            for (int i = 0; i < rareSetData.Length; i++)
            {
                BaseSetPart itemDisplayData = rareSetData[i];
                switch (itemDisplayData.PartName)
                {
                    case "Weapons":
                        FrSetManagerPublishInformation.GatheredWeapons = itemDisplayData.TotalSetsCount();
                        break;
                    case "Helmets":
                        FrSetManagerPublishInformation.GatheredHelmets = itemDisplayData.TotalSetsCount();
                        break;
                    case "Body Armors":
                        FrSetManagerPublishInformation.GatheredBodyArmors = itemDisplayData.TotalSetsCount();
                        break;
                    case "Gloves":
                        FrSetManagerPublishInformation.GatheredGloves = itemDisplayData.TotalSetsCount();
                        break;
                    case "Boots":
                        FrSetManagerPublishInformation.GatheredBoots = itemDisplayData.TotalSetsCount();
                        break;
                    case "Belts":
                        FrSetManagerPublishInformation.GatheredBelts = itemDisplayData.TotalSetsCount();
                        break;
                    case "Amulets":
                        FrSetManagerPublishInformation.GatheredAmulets = itemDisplayData.TotalSetsCount();
                        break;
                    case "Rings":
                        FrSetManagerPublishInformation.GatheredRings = itemDisplayData.TotalSetsCount();
                        break;
                }
            }

            PublishEvent("frsm_display_data", FrSetManagerPublishInformation);
            if (!_allowScanTabs)
            {
                if (_fixStopwatch.ElapsedMilliseconds > 3000)
                    _allowScanTabs = true; //fix for stashie doesn't send the finish drop items event
                return;
            }

            var needUpdate = UpdatePlayerInventory();
            var IngameState = GameController.Game.IngameState;
            var stashIsVisible = IngameState.IngameUi.StashElement.IsVisible &&
                                 IngameState.IngameUi.StashElement.VisibleStash != null;

            if (stashIsVisible)
                needUpdate = UpdateStashes() || needUpdate;

            if (needUpdate)
            {
                //Thread.Sleep(100);//Wait until item be placed to player invent. There should be some delay
                UpdateItemsSetsInfo();
            }

            if (!_bDropAllItems)
                DrawSetsInfo();

            RenderLabels();

            if (Settings.DropToInventoryKey.PressedOnce() && IngameState.IngameUi.InventoryPanel.IsVisible)
            {
                if (stashIsVisible)
                {
                    StartCoroutine(DropToStashCoroutine(), CoroutineNameDropToStash);
                }
                else if (IsNPCTradingWindowVisible())
                {
                    StartCoroutine(VendorCoroutine(), CoroutineNameVendor);
                }
            }
        }

        private void StartCoroutine(IEnumerator coroutine, string coroutineName)
        {
            var existing = Core.ParallelRunner.FindByName(coroutineName);
            if (existing != null && !existing.IsDone)
            {
                LogError($"Cancelling coroutine {coroutineName}");
                existing.Done(true);
                return;
            }

            _coroutineWorker = new Coroutine(coroutine, this, coroutineName);
            _coroutineWorker.WhenDone += (_, __) =>
            {
                Keyboard.KeyDown(Keys.LControlKey);
                Keyboard.KeyUp(Keys.LControlKey);
            };
            Core.ParallelRunner.Run(_coroutineWorker);
        }

        private IEnumerator VendorCoroutine()
        {
            LogMessage("Vendor coroutine", 3);
            var gameWindow = GameController.Window.GetWindowRectangle().TopLeft;
            var latency = (int)GameController.Game.IngameState.ServerData.Latency;

            var npcTradingWindow = GameController.Area.CurrentArea.IsHideout
                ? GameController.Game.IngameState.IngameUi.SellWindowHideout
                : GameController.Game.IngameState.IngameUi.SellWindow;

            if (!npcTradingWindow.IsVisible)
            {
                // The vendor sell window is not open, but is in memory (it would've went straigth to catch if that wasn't the case).
                LogMessage("[FRSM] NPC trading window not visible!", 5);
                yield break;
            }

            var playerOfferItems = npcTradingWindow.YourOffer;
            const int setItemsCount = 9;
            const int uiButtonsCount = 2;

            if (playerOfferItems.ChildCount < setItemsCount + uiButtonsCount)
            {
                for (var i = 0; i < 8; i++)
                {
                    var itemType = _itemSetTypes[i];
                    var items = itemType.GetPreparedItems();

                    if (items.Any(item => !item.BInPlayerInventory))
                        continue;

                    Keyboard.KeyDown(Keys.LControlKey);

                    foreach (var item in items)
                    {
                        var foundItem =
                            GameController.Game.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory]
                                .VisibleInventoryItems.FirstOrDefault(x =>
                                    x.InventPosX == item.InventPosX && x.InventPosY == item.InventPosY);

                        if (foundItem == null)
                        {
                            LogError($"[FRSM] Did not find item to place into trade: {item.ItemName}", 3);
                            yield break;
                        }

                        yield return Delay(INPUT_DELAY + Settings.ExtraDelay.Value);

                        var itemsInOfferAtStart = playerOfferItems.ChildCount;
                        var tryCount = 3;
                        do
                        {
                            yield return ClickElement(foundItem.GetClientRect().Center);
                            yield return Delay(INPUT_DELAY);
                            tryCount--;
                        } while (tryCount > 0 && playerOfferItems.ChildCount == itemsInOfferAtStart);

                        if (playerOfferItems.ChildCount == itemsInOfferAtStart)
                        {
                            LogError($"[FRSM] Failed to put item into trading window: {item.ItemName}", 3);
                            yield break;
                        }
                    }
                }

                Keyboard.KeyUp(Keys.LControlKey);
            }

            yield return Delay(latency + Settings.ExtraDelay.Value);

            var npcOfferItems = npcTradingWindow.OtherOffer;

            foreach (var element in npcOfferItems.Children)
            {
                var item = element.AsObject<NormalInventoryItem>().Item;

                if (string.IsNullOrEmpty(item.Metadata))
                    continue;

                var itemName = GameController.Files.BaseItemTypes.Translate(item.Metadata).BaseName;
                if (itemName == "Chaos Orb" || itemName == "Regal Orb") continue;
                LogMessage($"[FRSM] NPC offered {itemName} - cancelling sale'", 3);
                yield break;
            }

            yield return Delay(latency + Settings.ExtraDelay.Value);

            var acceptButton = npcTradingWindow.AcceptButton;
            Settings.SetsAmountStatistics++;
            Settings.SetsAmountStatisticsText = $"Total sets sold to vendor: {Settings.SetsAmountStatistics}";

            if (Settings.AutoSell.Value)
            {
                int tryCount = 3;
                do
                {
                    yield return ClickElement(acceptButton.GetClientRect().Center);
                    yield return Delay(INPUT_DELAY * 3);
                    tryCount--;
                } while (tryCount > 0 && npcTradingWindow.IsVisible);

                if (npcTradingWindow.IsVisible)
                {
                    LogError($"[FRSM] Failed to accept sale!");
                    yield break;
                }
            }
            else
                MoveMouseToElement(acceptButton.GetClientRect().Center);

            var coroutine = Core.ParallelRunner.FindByName(CoroutineNameVendor);
            coroutine?.Done();
        }

        private IEnumerator DropToStashCoroutine()
        {
            LogMessage("[FRSM] Dropping items to stash", 3);

            yield return Delay(25);

            var stashPanel = GameController.IngameState.IngameUi.StashElement;
            var stashNames = stashPanel.AllStashNames;
            var latency = (int)GameController.Game.IngameState.ServerData.Latency + Settings.ExtraDelay;
            var cursorStartPosition = Input.MousePosition;

            // Iterrate through all the different item types.
            for (var i = 0; i < 8; i++) //Check that we have enough items for any set
            {
                var part = _itemSetTypes[i];
                var items = part.GetPreparedItems();

                Keyboard.KeyDown(Keys.LControlKey);
                yield return Delay(INPUT_DELAY * 2);

                foreach (var curPreparedItem in items)
                {
                    // If items is already in our inventory, move on.
                    if (curPreparedItem.BInPlayerInventory)
                        continue;

                    // Get the index of the item we want to move from stash to inventory.
                    var invIndex = stashNames.IndexOf(curPreparedItem.StashName);

                    if (invIndex < 0)
                    {
                        LogError($"[FRSM] Invalid stash index: {invIndex}");
                    }

                    // Switch to the tab we want to go to.
                    yield return SwitchToTab(invIndex);

                    var tryCount = 5;
                    // Get the current visible stash tab.
                    do
                    {
                        _currentOpenedStashTab = stashPanel.VisibleStash;
                        yield return Delay(INPUT_DELAY);
                        tryCount--;
                    } while (tryCount > 0  && (_currentOpenedStashTab == null || !_currentOpenedStashTab.IsVisible ||
                                               !_currentOpenedStashTab.IsValid || _currentOpenedStashTab.VisibleInventoryItems.Count == 0));

                    if (_currentOpenedStashTab == null)
                    {
                        LogError($"[FRSM] Failed to switch stash to tab {invIndex}", 3);
                        yield break;
                    }

                    var item = curPreparedItem;

                    var foundItem =
                        _currentOpenedStashTab.VisibleInventoryItems.FirstOrDefault(
                            x => x.InventPosX == item.InventPosX && x.InventPosY == item.InventPosY);

                    if (foundItem == null)
                    {
                        LogError($"[FRSM] Failed to find item in stash to drop to inventory: {item.ItemName}", 3);
                        yield break;
                    }

                    LogMessage($"[FRSM] Dropping item: {item.ItemName}", 1);

                    tryCount = 10;
                    var baseName = string.Empty;
                    do
                    {
                        MoveMouseToElement(foundItem.GetClientRect().Center);
                        baseName = GameController?.IngameState?.UIHover?.Entity?.GetComponent<Base>()?.Name;
                        yield return Delay(INPUT_DELAY);
                        tryCount--;
                    } while (tryCount > 0 && !item.ItemName.Equals(baseName));

                    if (string.IsNullOrEmpty(baseName))
                    {
                        LogError(
                            $"[FRSM] Failed to to hover over item: {item.ItemName} from stash {item.StashName}", 3);
                        yield break;
                    }
                    
                    var itemsInStashAtStart = _currentOpenedStashTab.VisibleInventoryItems.Count;
                    tryCount = 3;
                    do
                    {
                        
                        yield return ClickElement(foundItem.GetClientRect().Center);
                        yield return Delay(INPUT_DELAY);
                        tryCount--;
                    } while (tryCount > 0 && _currentOpenedStashTab.VisibleInventoryItems.Count >= itemsInStashAtStart);

                    
                    if (_currentOpenedStashTab.VisibleInventoryItems.Count >= itemsInStashAtStart)
                    {
                        LogError(
                            $"[FRSM] Failed to drop item to inventory: {item.ItemName} from stash {item.StashName}", 3);
                        yield break;
                    }

                    item.BInPlayerInventory = true;

                    yield return Delay(latency + 25 + Settings.ExtraDelay.Value);

                    if (!UpdateStashes())
                    {
                        LogError("[FRSM] There was item drop but it don't want to update stash!", 3);
                        yield break;
                    }
                }

                Keyboard.KeyUp(Keys.LControlKey);
            }

            UpdatePlayerInventory();
            UpdateItemsSetsInfo();

            MoveMouseToElement(cursorStartPosition);

            var coroutine = Core.ParallelRunner.FindByName(CoroutineNameDropToStash);
            coroutine?.Done();
        }

        public bool IsNPCTradingWindowVisible()
        {
            return GameController.Game.IngameState.IngameUi.SellWindow.IsVisible ||
                   GameController.Game.IngameState.IngameUi.SellWindowHideout.IsVisible;
        }

        private void DrawSetsInfo()
        {
            var stash = GameController.IngameState.IngameUi.StashElement;
            var leftPanelOpened = stash.IsVisible;

            if (leftPanelOpened)
            {
                if (_currentSetData.BSetIsReady && _currentOpenedStashTab != null)
                {
                    var visibleInventoryItems = _currentOpenedStashTab.VisibleInventoryItems;

                    if (visibleInventoryItems != null)
                    {
                        var stashTabRect = _currentOpenedStashTab.InventoryUIElement.GetClientRect();

                        var setItemsListRect = new RectangleF(stashTabRect.Right, stashTabRect.Bottom, 270, 240);
                        Graphics.DrawBox(setItemsListRect, new Color(0, 0, 0, 200));
                        Graphics.DrawFrame(setItemsListRect, Color.White, 2);

                        var drawPosX = setItemsListRect.X + 10;
                        var drawPosY = setItemsListRect.Y + 10;

                        Graphics.DrawText("Current " + (_currentSetData.SetType == 1 ? "Chaos" : "Regal") + " set:",
                            new Vector2(drawPosX, drawPosY),
                            Color.White, 15);

                        drawPosY += 25;

                        for (var i = 0; i < 8; i++)
                        {
                            var part = _itemSetTypes[i];
                            var items = part.GetPreparedItems();

                            foreach (var curPreparedItem in items)
                            {
                                var inInventory = _sData.PlayerInventory.StashTabItems.Contains(curPreparedItem);
                                var curStashOpened = curPreparedItem.StashName == _currentOpenedStashTabName;
                                var color = Color.Gray;

                                if (inInventory)
                                    color = Color.Green;
                                else if (curStashOpened)
                                    color = Color.Yellow;

                                if (!inInventory && curStashOpened)
                                {
                                    var item = curPreparedItem;

                                    var foundItem =
                                        visibleInventoryItems.FirstOrDefault(x =>
                                            x.InventPosX == item.InventPosX && x.InventPosY == item.InventPosY);

                                    if (foundItem != null)
                                        Graphics.DrawFrame(foundItem.GetClientRect(), Color.Yellow, 2);
                                }

                                Graphics.DrawText(
                                    curPreparedItem.StashName + " (" + curPreparedItem.ItemName + ") " +
                                    (curPreparedItem.LowLvl ? "L" : "H"), new Vector2(drawPosX, drawPosY), color, 15);

                                drawPosY += 20;
                            }
                        }
                    }
                }
            }

            if (Settings.ShowOnlyWithInventory)
            {
                if (!GameController.Game.IngameState.IngameUi.InventoryPanel.IsVisible)
                    return;
            }

            if (Settings.HideWhenLeftPanelOpened)
            {
                if (leftPanelOpened)
                    return;
            }

            var posX = Settings.PositionX.Value;
            var posY = Settings.PositionY.Value;

            var rect = new RectangleF(posX, posY, 230 * Settings.WidthMultiplier, 280 * Settings.HeightMultiplier);
            Graphics.DrawBox(rect, new Color(0, 0, 0, 200));
            Graphics.DrawFrame(rect, Color.White, 2);

            posX += 10;
            posY += 10;
            Graphics.DrawText(_drawInfoString, new Vector2(posX, posY), Color.White, 15);
        }

        private void UpdateItemsSetsInfo()
        {
            _currentSetData = new CurrentSetInfo();

            _itemSetTypes = new BaseSetPart[8];
            _itemSetTypes[0] = new WeaponItemsSetPart("Weapons") { ItemCellsSize = 8 };
            _itemSetTypes[1] = new SingleItemSetPart("Helmets") { ItemCellsSize = 4 };
            _itemSetTypes[2] = new SingleItemSetPart("Body Armors") { ItemCellsSize = 6 };
            _itemSetTypes[3] = new SingleItemSetPart("Gloves") { ItemCellsSize = 4 };
            _itemSetTypes[4] = new SingleItemSetPart("Boots") { ItemCellsSize = 4 };
            _itemSetTypes[5] = new SingleItemSetPart("Belts") { ItemCellsSize = 2 };
            _itemSetTypes[6] = new SingleItemSetPart("Amulets") { ItemCellsSize = 1 };
            _itemSetTypes[7] = new RingItemsSetPart("Rings") { ItemCellsSize = 1 };

            for (var i = 0; i <= 7; i++)
            {
                DisplayData[i].BaseData = _itemSetTypes[i];
            }

            foreach (var item in _sData.PlayerInventory.StashTabItems)
            {
                var index = (int)item.ItemType;

                if (index > 7)
                    index = 0; // Switch One/TwoHanded to 0(weapon)

                var setPart = _itemSetTypes[index];
                item.BInPlayerInventory = true;
                setPart.AddItem(item);
            }

            const int StashCellsCount = 12 * 12;

            foreach (var stash in _sData.StashTabs)
            {
                var stashTabItems = stash.Value.StashTabItems;

                foreach (var item in stashTabItems)
                {
                    var index = (int)item.ItemType;

                    if (index > 7)
                        index = 0; // Switch One/TwoHanded to 0(weapon)

                    var setPart = _itemSetTypes[index];
                    item.BInPlayerInventory = false;
                    setPart.AddItem(item);
                    setPart.StashTabItemsCount = stashTabItems.Count;
                }
            }

            //Calculate sets:
            _drawInfoString = "";
            var chaosSetMaxCount = 0;

            var regalSetMaxCount = int.MaxValue;
            var minItemsCount = int.MaxValue;
            var maxItemsCount = 0;

            for (var i = 0; i <= 7; i++) //Check that we have enough items for any set
            {
                var setPart = _itemSetTypes[i];

                var low = setPart.LowSetsCount();
                var high = setPart.HighSetsCount();
                var total = setPart.TotalSetsCount();

                if (minItemsCount > total)
                    minItemsCount = total;

                if (maxItemsCount < total)
                    maxItemsCount = total;

                if (regalSetMaxCount > high)
                    regalSetMaxCount = high;

                chaosSetMaxCount += low;
                _drawInfoString += setPart.GetInfoString() + "\r\n";

                var drawInfo = DisplayData[i];
                drawInfo.TotalCount = total;
                drawInfo.TotalLowCount = low;
                drawInfo.TotalHighCount = high;

                if (Settings.CalcByFreeSpace.Value)
                {
                    var totalPossibleStashItemsCount = StashCellsCount / setPart.ItemCellsSize;

                    drawInfo.FreeSpaceCount = totalPossibleStashItemsCount -
                                              (setPart.StashTabItemsCount + setPart.PlayerInventItemsCount());

                    if (drawInfo.FreeSpaceCount < 0)
                        drawInfo.FreeSpaceCount = 0;

                    drawInfo.PriorityPercent = (float)drawInfo.FreeSpaceCount / totalPossibleStashItemsCount;

                    if (drawInfo.PriorityPercent > 1)
                        drawInfo.PriorityPercent = 1;

                    drawInfo.PriorityPercent = 1 - drawInfo.PriorityPercent;
                }
            }

            if (!Settings.CalcByFreeSpace.Value)
            {
                var maxSets = maxItemsCount;

                if (Settings.MaxSets.Value > 0)
                    maxSets = Settings.MaxSets.Value;

                for (var i = 0; i <= 7; i++)
                {
                    var drawInfo = DisplayData[i];

                    if (drawInfo.TotalCount == 0)
                        drawInfo.PriorityPercent = 0;
                    else
                    {
                        drawInfo.PriorityPercent = (float)drawInfo.TotalCount / maxSets;

                        if (drawInfo.PriorityPercent > 1)
                            drawInfo.PriorityPercent = 1;
                    }
                }
            }

            _drawInfoString += "\r\n";

            var chaosSets = Math.Min(minItemsCount, chaosSetMaxCount);

            _drawInfoString += "Chaos sets ready: " + chaosSets;

            if (Settings.ShowRegalSets.Value)
            {
                _drawInfoString += "\r\n";
                _drawInfoString += "Regal sets ready: " + regalSetMaxCount;
            }

            if (chaosSets <= 0 && regalSetMaxCount <= 0)
                return;
            if (chaosSets <= 0 && Settings.OptimizeChaosSets.Value)
                return;

            {
                var maxAvailableReplaceCount = 0;
                var replaceIndex = -1;

                var isLowSet = false;

                for (var i = 0; i < 8; i++) //Check that we have enough items for any set
                {
                    var part = _itemSetTypes[i];
                    var prepareResult = part.PrepareItemForSet(Settings);

                    isLowSet = isLowSet || prepareResult.LowSet;

                    if (maxAvailableReplaceCount >= prepareResult.AllowedReplacesCount || prepareResult.BInPlayerInvent)
                        continue;

                    maxAvailableReplaceCount = prepareResult.AllowedReplacesCount;
                    replaceIndex = i;
                }

                if (!isLowSet)
                {
                    if (Settings.ShowRegalSets)
                    {
                        _currentSetData.BSetIsReady = true;
                        _currentSetData.SetType = 2;
                        return;
                    }

                    if (maxAvailableReplaceCount == 0)
                    {
                        //LogMessage("You want to make a regal set anyway? Ok.", 2);
                        _currentSetData.BSetIsReady = true;
                        _currentSetData.SetType = 2;
                        return;
                    }

                    if (replaceIndex != -1)
                    {
                        _itemSetTypes[replaceIndex].DoLowItemReplace();
                        _currentSetData.SetType = 1;
                        _currentSetData.BSetIsReady = true;
                    }
                    else
                    {
                        _currentSetData.BSetIsReady = true;
                        _currentSetData.SetType = 1;
                    }
                }
                else
                {
                    _currentSetData.BSetIsReady = true;
                    _currentSetData.SetType = 1;
                }
            }
        }

        public bool UpdateStashes()
        {
            var stashPanel = GameController.IngameState.IngameUi.StashElement;

            if (stashPanel == null)
            {
                LogMessage("ServerData.StashPanel is null", 3);
                return false;
            }

            var needUpdateAllInfo = false;
            _currentOpenedStashTabName = "";
            _currentOpenedStashTab = stashPanel.VisibleStash;

            if (_currentOpenedStashTab == null)
                return false;

            for (var i = 0; i < stashPanel.TotalStashes; i++)
            {
                var stashName = stashPanel.GetStashName(i);

                if (Settings.OnlyAllowedStashTabs.Value)
                {
                    if (!Settings.AllowedStashTabs.Contains(i))
                        continue;
                }

                var stash = stashPanel.GetStashInventoryByIndex(i);

                var visibleInventoryItems = stash?.VisibleInventoryItems;

                if (visibleInventoryItems == null)
                    continue;

                if (_currentOpenedStashTab.Address == stash.Address)
                    _currentOpenedStashTabName = stashName;

                var add = false;

                if (!_sData.StashTabs.TryGetValue(stashName, out var curStashData))
                {
                    curStashData = new StashTabData();
                    add = true;
                }

                var items = new List<StashItem>();
                needUpdateAllInfo = true;

                foreach (var invItem in visibleInventoryItems)
                {
                    var item = invItem.Item;
                    var newStashItem = ProcessItem(item);

                    if (newStashItem == null)
                    {
                        if (Settings.ShowRedRectangleAroundIgnoredItems)
                            Graphics.DrawFrame(invItem.GetClientRect(), Color.Red, 2);

                        continue;
                    }

                    newStashItem.StashName = stashName;
                    newStashItem.InventPosX = invItem.InventPosX;
                    newStashItem.InventPosY = invItem.InventPosY;
                    newStashItem.BInPlayerInventory = false;
                    items.Add(newStashItem);
                }

                if (_currentOpenedStashTab.Address == stash.Address) //in case tab was closed before we finish update
                {
                    curStashData.StashTabItems = items;
                    curStashData.ItemsCount = (int)stash.ItemCount;
                }

                if (add && curStashData.ItemsCount > 0)
                    _sData.StashTabs.Add(stashName, curStashData);
            }

            if (!needUpdateAllInfo)
                return false;

            return true;
        }

        private bool UpdatePlayerInventory()
        {
            //    if (!GameController.Game.IngameState.IngameUi.InventoryPanel.IsVisible)
            //        return false;

            var inventory = GameController.Game.IngameState.ServerData.PlayerInventories[0].Inventory;

            if (_sData?.PlayerInventory == null)
                return true;

            _sData.PlayerInventory = new StashTabData();

            var invItems = inventory;

            if (invItems == null) return true;

            foreach (var invItem in invItems.InventorySlotItems)
            {
                var item = invItem;
                var newAddedItem = ProcessItem(item.Item);

                if (newAddedItem == null) continue;
                newAddedItem.InventPosX = (int)invItem.InventoryPosition.X;
                newAddedItem.InventPosY = (int)invItem.InventoryPosition.Y;
                newAddedItem.BInPlayerInventory = true;
                _sData.PlayerInventory.StashTabItems.Add(newAddedItem);
            }

            _sData.PlayerInventory.ItemsCount = (int)inventory.TotalItemsCounts;

            return true;
        }

        private StashItem ProcessItem(Entity item)
        {
            try
            {
                if (item == null) return null;

                var mods = item?.GetComponent<Mods>();

                if (mods?.ItemRarity != ItemRarity.Rare)
                    return null;

                var bIdentified = mods.Identified;

                if (bIdentified && !Settings.AllowIdentified)
                    return null;

                if (mods.ItemLevel < 60)
                    return null;

                var newItem = new StashItem
                {
                    BIdentified = bIdentified,
                    LowLvl = mods.ItemLevel < 75
                };

                if (string.IsNullOrEmpty(item.Metadata))
                {
                    LogError("Item metadata is empty. Can be fixed by restarting the game", 10);
                    return null;
                }

                if (Settings.IgnoreElderShaper.Value)
                {
                    var baseComp = item.GetComponent<Base>();

                    if (baseComp.isElder || baseComp.isShaper)
                        return null;
                }

                var bit = GameController.Files.BaseItemTypes.Translate(item.Metadata);

                if (bit == null)
                    return null;

                newItem.ItemClass = bit.ClassName;
                newItem.ItemName = bit.BaseName;
                newItem.ItemType = GetStashItemTypeByClassName(newItem.ItemClass);
                newItem.Width = bit.Width;
                newItem.Height = bit.Height;

                if (newItem.ItemType != StashItemType.Undefined)
                    return newItem;
            }
            catch (Exception e)
            {
                LogError($"Error in \"ProcessItem\": {e}", 10);
                return null;
            }

            return null;
        }

        private StashItemType GetStashItemTypeByClassName(string className)
        {
            if (className.StartsWith("Two Hand"))
                return StashItemType.TwoHanded;

            if (className.StartsWith("One Hand") || className.StartsWith("Thrusting One Hand"))
                return StashItemType.OneHanded;

            switch (className)
            {
                case "Wand": return StashItemType.OneHanded;
                case "Dagger": return StashItemType.OneHanded;
                case "Rune Dagger": return StashItemType.OneHanded;
                case "Sceptre": return StashItemType.OneHanded;
                case "Claw": return StashItemType.OneHanded;
                case "Shield": return StashItemType.OneHanded;
                case "Bow": return StashItemType.TwoHanded;
                case "Staff": return StashItemType.TwoHanded;
                case "Warstaff": return StashItemType.TwoHanded;

                case "Ring": return StashItemType.Ring;
                case "Amulet": return StashItemType.Amulet;
                case "Belt": return StashItemType.Belt;

                case "Helmet": return StashItemType.Helmet;
                case "Body Armour": return StashItemType.Body;
                case "Boots": return StashItemType.Boots;
                case "Gloves": return StashItemType.Gloves;

                default:
                    return StashItemType.Undefined;
            }
        }

        public override void DrawSettings()
        {
            base.DrawSettings();
            var stashPanel = GameController.Game.IngameState.IngameUi.StashElement;
            var realNames = stashPanel.AllStashNames;

            var uniqId = 0;

            if (ImGui.Button($"Add##{uniqId++}"))
            {
                Settings.AllowedStashTabs.Add(-1);
            }

            for (var i = 0; i < Settings.AllowedStashTabs.Count; i++)
            {
                var value = Settings.AllowedStashTabs[i];

                if (ImGui.Combo(value < realNames.Count && value >= 0 ? realNames[value] : "??", ref value,
                    realNames.ToArray(), realNames.Count))
                {
                    Settings.AllowedStashTabs[i] = value;
                }

                ImGui.SameLine();

                if (ImGui.Button($"Remove##{uniqId++}"))
                {
                    Settings.AllowedStashTabs.RemoveAt(i);
                    i--;
                }
            }
        }

        public override void OnClose()
        {
            if (_sData != null)
                StashData.Save(this, _sData);
        }

        private struct CurrentSetInfo
        {
            public bool BSetIsReady;
            public int SetType; // 1 - chaos set, 2 - regal set
        }
        /*Rare set classes:

        Two Hand Sword
        Two Hand Axe
        Two Hand Mace
        Bow
        Staff

        One Hand Sword
        One Hand Axe
        One Hand Mace
        Sceptre
        Wand
        Dagger
        Claw

        Ring
        Amulet
        Belt
        Shield
        Helmet
        Body Armour
        Boots
        Gloves
        */

        public class ItemDisplayData
        {
            public BaseSetPart BaseData;
            public int FreeSpaceCount;
            public float PriorityPercent;
            public int TotalCount;
            public int TotalHighCount;
            public int TotalLowCount;
        }

        #region Draw labels

        private readonly Dictionary<Entity, ItemDisplayData> _currentAlerts =
            new Dictionary<Entity, ItemDisplayData>();

        private Dictionary<long, LabelOnGround> _currentLabels =
            new Dictionary<long, LabelOnGround>();

        private void RenderLabels()
        {
            if (!Settings.EnableBorders.Value)
                return;

            var shouldUpdate = false;

            var tempCopy = new Dictionary<Entity, ItemDisplayData>(_currentAlerts);

            var keyValuePairs = tempCopy.AsParallel().Where(x => x.Key != null && x.Key.Address != 0 && x.Key.IsValid)
                .ToList();

            foreach (var kv in keyValuePairs)
            {
                if (DrawBorder(kv.Key.Address, kv.Value) && !shouldUpdate)
                    shouldUpdate = true;
            }

            if (shouldUpdate)
            {
                _currentLabels = GameController.Game.IngameState.IngameUi.ItemsOnGroundLabels
                    .Where(y => y?.ItemOnGround != null).GroupBy(y => y.ItemOnGround.Address)
                    .ToDictionary(y => y.Key, y => y.First());
            }

            if (!Settings.InventBorders.Value)
                return;

            if (!GameController.Game.IngameState.IngameUi.InventoryPanel.IsVisible)
                return;

            var playerInv = GameController.Game.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory];
            var visibleInventoryItems = playerInv.VisibleInventoryItems;

            if (visibleInventoryItems == null || visibleInventoryItems.Count == 0)
                return;

            if (playerInv.HoverItem != null && !Settings.LablesWhileHovered)
                return;

            foreach (var inventItem in visibleInventoryItems)
            {
                var item = inventItem.Item;

                if (item == null)
                    continue;

                var visitResult = ProcessItem(item);

                if (visitResult == null) continue;
                var index = (int)visitResult.ItemType;

                if (index > 7)
                    index = 0;

                var data = DisplayData[index];
                var rect = inventItem.GetClientRect();

                var borderColor = Color.Lerp(Color.Red, Color.Green, data.PriorityPercent);

                rect.X += 2;
                rect.Y += 2;

                rect.Width -= 4;
                rect.Height -= 4;

                var testRect = new RectangleF(rect.X + 3, rect.Y + 3, 40, 20);

                Graphics.DrawBox(testRect, new Color(10, 10, 10, 230));
                Graphics.DrawFrame(rect, borderColor, 2);

                Graphics.DrawText(
                    Settings.CalcByFreeSpace.Value ? $"{data.FreeSpaceCount}" : $"{data.PriorityPercent:p0}",
                    testRect.TopLeft,
                    Color.White,
                    Settings.TextSize.Value);
            }
        }

        private bool DrawBorder(long entityAddress, ItemDisplayData data)
        {
            if (GameController.Game.IngameState.IngameUi.Atlas.IsVisible)
                return false;

            if (GameController.Game.IngameState.IngameUi.BetrayalWindow.IsVisible)
                return false;

            if (GameController.Game.IngameState.IngameUi.CraftBench.IsVisible)
                return false;

            if (GameController.Game.IngameState.IngameUi.DelveWindow.IsVisible)
                return false;

            if (GameController.Game.IngameState.IngameUi.IncursionWindow.IsVisible)
                return false;

            /*
            if (GameController.Game.IngameState.IngameUi.MetamorphWindow.IsVisible)
                return false;
            */

            if (GameController.Game.IngameState.IngameUi.TreePanel.IsVisible)
                return false;

            if (GameController.Game.IngameState.IngameUi.UnveilWindow.IsVisible)
                return false;

            if (GameController.Game.IngameState.IngameUi.ZanaMissionChoice.IsVisible)
                return false;

            if (Settings.Ignore1 && data.PriorityPercent == 1 &&
                !(data.BaseData.PartName == "Amulets" || data.BaseData.PartName == "Rings")) return false;

            var ui = GameController.Game.IngameState.IngameUi;
            var shouldUpdate = false;

            if (_currentLabels.TryGetValue(entityAddress, out var entityLabel))
            {
                if (!entityLabel.IsVisible) return shouldUpdate;

                var rect = entityLabel.Label.GetClientRect();

                if (ui.OpenLeftPanel.IsVisible && ui.OpenLeftPanel.GetClientRect().Intersects(rect) ||
                    ui.OpenRightPanel.IsVisible && ui.OpenRightPanel.GetClientRect().Intersects(rect))
                    return false;

                var incrSize = Settings.BorderOversize.Value;

                if (Settings.BorderAutoResize.Value)
                    incrSize = (int)Lerp(incrSize, 1, data.PriorityPercent);

                rect.X -= incrSize;
                rect.Y -= incrSize;

                rect.Width += incrSize * 2;
                rect.Height += incrSize * 2;

                var borderColor = Color.Lerp(Color.Red, Color.Green, data.PriorityPercent);

                var borderWidth = Settings.BorderWidth.Value;

                if (Settings.BorderAutoResize.Value)
                    borderWidth = (int)Lerp(borderWidth, 1, data.PriorityPercent);

                Graphics.DrawFrame(rect, borderColor, borderWidth);

                if (Settings.TextSize.Value == 0) return shouldUpdate;

                if (Settings.TextOffsetX < 0)
                    rect.X += Settings.TextOffsetX;
                else
                    rect.X += rect.Width * (Settings.TextOffsetX.Value / 10);

                if (Settings.TextOffsetY < 0)
                    rect.Y += Settings.TextOffsetY;
                else
                    rect.Y += rect.Height * (Settings.TextOffsetY.Value / 10);

                Graphics.DrawText(
                    Settings.CalcByFreeSpace.Value ? $"{data.FreeSpaceCount}" : $"{data.PriorityPercent:p0}",
                    rect.TopLeft,
                    Color.White,
                    Settings.TextSize.Value
                );
            }
            else
                shouldUpdate = true;

            return shouldUpdate;
        }

        private float Lerp(float a, float b, float f)
        {
            return a + f * (b - a);
        }

        #endregion
    }
}