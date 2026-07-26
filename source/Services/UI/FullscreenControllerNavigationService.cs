using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Services.UI
{
    internal sealed class FullscreenControllerNavigationService : IDisposable
    {
        private static readonly TimeSpan InitialInputBlock = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan RepeatedInputBlock = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan DirectionalInputBlock = TimeSpan.FromMilliseconds(250);
        private const string CellFocusTag = "ControllerCellFocus";

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly Dictionary<Window, IFullscreenControllerNavigable> _registeredWindows =
            new Dictionary<Window, IFullscreenControllerNavigable>();
        private readonly Dictionary<Window, DateTime> _inputBlockedUntilUtc =
            new Dictionary<Window, DateTime>();
        private readonly Dictionary<Window, HandledControllerInput> _lastHandledInput =
            new Dictionary<Window, HandledControllerInput>();

        public FullscreenControllerNavigationService(IPlayniteAPI api, ILogger logger)
        {
            _api = api;
            _logger = logger;
        }

        private Dispatcher UiDispatcher =>
            _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        public void RegisterWindow(Window window, IFullscreenControllerNavigable controllerHandler)
        {
            if (window == null || controllerHandler == null)
            {
                return;
            }

            void unregister()
            {
                _registeredWindows.Remove(window);
                _inputBlockedUntilUtc.Remove(window);
                _lastHandledInput.Remove(window);
            }

            RoutedEventHandler loadedHandler = null;
            loadedHandler = (_, __) =>
            {
                window.Loaded -= loadedHandler;
                BlockInputBriefly(window);
                PrepareWindowForControllerFocus(window);
                EnsureFocusInsideWindow(window);
            };

            EventHandler activatedHandler = null;
            activatedHandler = (_, __) =>
            {
                BlockInputBriefly(window);
                PrepareWindowForControllerFocus(window);
                EnsureFocusInsideWindow(window);
            };

            KeyEventHandler previewKeyDownHandler = (s, e) =>
            {
                if (IsDirectionalKey(e.Key) && IsMatchingControllerDirectionHandled(window, e.Key))
                {
                    e.Handled = true;
                }
            };

            EventHandler closedHandler = null;
            closedHandler = (_, __) =>
            {
                window.Closed -= closedHandler;
                window.Loaded -= loadedHandler;
                window.Activated -= activatedHandler;
                window.PreviewKeyDown -= previewKeyDownHandler;
                unregister();
            };

            _registeredWindows[window] = controllerHandler;
            BlockInputBriefly(window);
            window.Loaded += loadedHandler;
            window.Activated += activatedHandler;
            window.Closed += closedHandler;
            window.PreviewKeyDown += previewKeyDownHandler;

            if (window.IsLoaded)
            {
                PrepareWindowForControllerFocus(window);
                EnsureFocusInsideWindow(window);
            }
        }

        private bool IsMatchingControllerDirectionHandled(Window window, Key key)
        {
            if (window == null || !_lastHandledInput.TryGetValue(window, out var lastInput))
            {
                return false;
            }

            if (DateTime.UtcNow - lastInput.HandledAtUtc > DirectionalInputBlock)
            {
                return false;
            }

            return key switch
            {
                Key.Up => lastInput.InputKey == "Up",
                Key.Down => lastInput.InputKey == "Down",
                Key.Left => lastInput.InputKey == "Left",
                Key.Right => lastInput.InputKey == "Right",
                _ => false
            };
        }

        public bool TryHandleControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            if (args == null || args.State != ControllerInputState.Pressed)
            {
                return false;
            }

            try
            {
                var dispatcher = UiDispatcher;
                if (dispatcher == null || dispatcher.CheckAccess())
                {
                    return TryHandleControllerInputOnUiThread(args.Button);
                }

                return dispatcher.Invoke(() => TryHandleControllerInputOnUiThread(args.Button));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to route fullscreen controller input.");
                return false;
            }
        }

        public void Dispose()
        {
            _registeredWindows.Clear();
            _inputBlockedUntilUtc.Clear();
            _lastHandledInput.Clear();
        }

        private bool TryHandleControllerInputOnUiThread(ControllerInput input)
        {
            if (!IsFullscreenMode())
            {
                return false;
            }

            var activeWindow = GetActiveWindow();
            IFullscreenControllerNavigable controllerHandler = null;
            if (activeWindow == null ||
                !_registeredWindows.TryGetValue(activeWindow, out controllerHandler))
            {
                activeWindow = GetRegisteredWindowOwningKeyboardFocus();
                if (activeWindow != null)
                {
                    _registeredWindows.TryGetValue(activeWindow, out controllerHandler);
                }
            }

            if (activeWindow == null || controllerHandler == null)
            {
                return false;
            }

            if (IsInputBrieflyBlocked(activeWindow))
            {
                return true;
            }

            if (!IsKeyboardFocusOwnedByWindow(activeWindow))
            {
                EnsureFocusInsideWindow(activeWindow);
                if (!IsKeyboardFocusOwnedByWindow(activeWindow))
                {
                    return true;
                }
            }

            var inputKey = NormalizeInputKey(input);
            if (IsRepeatedInput(activeWindow, inputKey))
            {
                return true;
            }

            var openContextMenu = FindOpenContextMenuForWindow(activeWindow);
            if (openContextMenu?.IsOpen == true)
            {
                if (TryRouteControllerInputToContextMenu(openContextMenu, input))
                {
                    RecordHandledInput(activeWindow, inputKey);
                    return true;
                }

                return false;
            }

            PrepareWindowForControllerFocus(activeWindow);

            if (input == ControllerInput.DPadUp || input == ControllerInput.DPadDown ||
                input == ControllerInput.DPadLeft || input == ControllerInput.DPadRight ||
                input == ControllerInput.LeftStickUp || input == ControllerInput.LeftStickDown ||
                input == ControllerInput.LeftStickLeft || input == ControllerInput.LeftStickRight)
            {
                if (TryHandleTextBoxArrowEscape(input))
                {
                    RecordHandledInput(activeWindow, inputKey);
                    return true;
                }
            }

            if (!controllerHandler.HandleFullscreenControllerInput(input))
            {
                return false;
            }

            RecordHandledInput(activeWindow, inputKey);
            return true;
        }

        private bool TryHandleTextBoxArrowEscape(ControllerInput input)
        {
            if (!(Keyboard.FocusedElement is TextBox textBox))
            {
                return false;
            }

            if (IsAchievementIconOverrideTextBox(textBox))
            {
                return false;
            }

            var direction = FocusNavigationDirection.Down;
            if (input == ControllerInput.DPadUp || input == ControllerInput.LeftStickUp) direction = FocusNavigationDirection.Up;
            else if (input == ControllerInput.DPadDown || input == ControllerInput.LeftStickDown) direction = FocusNavigationDirection.Down;
            else if (input == ControllerInput.DPadLeft || input == ControllerInput.LeftStickLeft) direction = FocusNavigationDirection.Left;
            else if (input == ControllerInput.DPadRight || input == ControllerInput.LeftStickRight) direction = FocusNavigationDirection.Right;

            // For multiline textboxes, we might want to check caret position, 
            // but for simple search boxes, we just want to escape.
            if (!textBox.AcceptsReturn || 
                (direction == FocusNavigationDirection.Up && textBox.GetLineIndexFromCharacterIndex(textBox.CaretIndex) == 0) ||
                (direction == FocusNavigationDirection.Down && textBox.GetLineIndexFromCharacterIndex(textBox.CaretIndex) == textBox.LineCount - 1))
            {
                return textBox.MoveFocus(new TraversalRequest(direction));
            }

            return false;
        }

        private static bool IsAchievementIconOverrideTextBox(TextBox textBox)
        {
            var tag = textBox?.Tag as string;
            return string.Equals(tag, "Unlocked", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "Locked", StringComparison.OrdinalIgnoreCase);
        }

        private void BlockInputBriefly(Window window)
        {
            if (window != null)
            {
                _inputBlockedUntilUtc[window] = DateTime.UtcNow.Add(InitialInputBlock);
            }
        }

        private bool IsInputBrieflyBlocked(Window window)
        {
            if (window == null ||
                !_inputBlockedUntilUtc.TryGetValue(window, out var blockedUntilUtc))
            {
                return false;
            }

            if (DateTime.UtcNow < blockedUntilUtc)
            {
                return true;
            }

            _inputBlockedUntilUtc.Remove(window);
            return false;
        }

        private bool IsRepeatedInput(Window window, string inputKey)
        {
            if (window == null ||
                string.IsNullOrEmpty(inputKey) ||
                !_lastHandledInput.TryGetValue(window, out var lastInput) ||
                !string.Equals(lastInput.InputKey, inputKey, StringComparison.Ordinal))
            {
                return false;
            }

            var block = IsDirectionalInputKey(inputKey) ? DirectionalInputBlock : RepeatedInputBlock;
            return DateTime.UtcNow - lastInput.HandledAtUtc < block;
        }

        private void RecordHandledInput(Window window, string inputKey)
        {
            if (window != null && !string.IsNullOrEmpty(inputKey))
            {
                _lastHandledInput[window] = new HandledControllerInput(inputKey, DateTime.UtcNow);
            }
        }

        private bool IsFullscreenMode()
        {
            try
            {
                return _api?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
            }
            catch
            {
                return false;
            }
        }

        private static Window GetActiveWindow()
        {
            return Application.Current?.Windows?
                .OfType<Window>()
                .FirstOrDefault(window => window.IsActive);
        }

        private Window GetRegisteredWindowOwningKeyboardFocus()
        {
            return _registeredWindows.Keys.FirstOrDefault(IsKeyboardFocusOwnedByWindow);
        }

        private bool IsKeyboardFocusOwnedByWindow(Window window)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null)
            {
                return false;
            }

            if (ReferenceEquals(Window.GetWindow(focused), window))
            {
                return true;
            }

            var contextMenu = FindAncestor<ContextMenu>(focused);
            if (contextMenu?.IsOpen == true &&
                IsPlacementTargetInsideWindow(contextMenu.PlacementTarget, window))
            {
                return true;
            }

            var popup = FindAncestor<Popup>(focused);
            return popup?.IsOpen == true &&
                   IsPlacementTargetInsideWindow(popup.PlacementTarget, window);
        }

        private static bool IsPlacementTargetInsideWindow(UIElement placementTarget, Window window)
        {
            return placementTarget != null &&
                   window != null &&
                   ReferenceEquals(Window.GetWindow(placementTarget), window);
        }

        internal static bool IsAcceptInput(ControllerInput input)
        {
            return input == ControllerInput.A;
        }

        internal static bool IsBackInput(ControllerInput input)
        {
            return input == ControllerInput.B || input == ControllerInput.Back;
        }

        internal static bool IsSecondaryClickInput(ControllerInput input)
        {
            return input == ControllerInput.X;
        }

        internal static bool IsLeftShoulderInput(ControllerInput input)
        {
            return input == ControllerInput.LeftShoulder;
        }

        internal static bool IsRightShoulderInput(ControllerInput input)
        {
            return input == ControllerInput.RightShoulder;
        }

        internal static bool FocusDataGrid(DataGrid grid, int? preferredIndex = null)
        {
            if (grid == null)
            {
                return false;
            }

            if (grid.Items?.Count > 0)
            {
                var index = preferredIndex ?? grid.SelectedIndex;
                if (index < 0)
                {
                    index = 0;
                }

                index = Math.Max(0, Math.Min(grid.Items.Count - 1, index));
                grid.SelectedIndex = index;
                grid.UpdateLayout();
                grid.ScrollIntoView(grid.Items[index]);
                grid.UpdateLayout();

                var row = grid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow;
                if (row != null)
                {
                    if (UsesControllerCellFocus(grid) && FocusDataGridCell(grid, row, index))
                    {
                        return true;
                    }

                    return FocusElement(row);
                }
            }

            return FocusElement(grid);
        }

        internal static bool TryGetVerticalDelta(ControllerInput input, out int delta)
        {
            if (input == ControllerInput.DPadUp || input == ControllerInput.LeftStickUp)
            {
                delta = -1;
                return true;
            }

            if (input == ControllerInput.DPadDown || input == ControllerInput.LeftStickDown)
            {
                delta = 1;
                return true;
            }

            delta = 0;
            return false;
        }

        internal static bool TryGetHorizontalDelta(ControllerInput input, out int delta)
        {
            if (input == ControllerInput.DPadLeft || input == ControllerInput.LeftStickLeft)
            {
                delta = -1;
                return true;
            }

            if (input == ControllerInput.DPadRight || input == ControllerInput.LeftStickRight)
            {
                delta = 1;
                return true;
            }

            delta = 0;
            return false;
        }

        internal static bool ActivateFocusedDataGridColumnHeader(DataGrid grid)
        {
            var header = GetFocusedDataGridColumnHeader(grid);
            return header != null && ActivateElement(header);
        }

        internal static bool FocusDataGridColumnHeader(DataGrid grid)
        {
            if (grid == null)
            {
                return false;
            }

            var headers = GetVisibleDataGridColumnHeaders(grid);
            if (headers.Count == 0)
            {
                return false;
            }

            var currentColumn = GetFocusedDataGridColumn(grid);
            var targetHeader = currentColumn != null
                ? headers.FirstOrDefault(header => ReferenceEquals(header.Column, currentColumn))
                : null;

            return FocusElement(targetHeader ?? headers.First());
        }

        internal static bool IsFocusAtDataGridLeftEdge(DataGrid grid)
        {
            if (grid == null)
            {
                return false;
            }

            var header = GetFocusedDataGridColumnHeader(grid);
            if (header?.Column != null)
            {
                return IsFirstVisibleColumn(grid, header.Column);
            }

            var focused = Keyboard.FocusedElement as DependencyObject;
            var cell = FindAncestor<DataGridCell>(focused);
            if (cell != null && IsDescendantOf(cell, grid))
            {
                return IsFirstVisibleColumn(grid, cell.Column);
            }

            var focusedRow = FindAncestor<DataGridRow>(focused);
            return ReferenceEquals(focused, grid) ||
                   focusedRow != null && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(focusedRow), grid);
        }

        internal static bool MoveFocus(FocusNavigationDirection direction, DependencyObject containmentRoot = null)
        {
            var focusedInput = Keyboard.FocusedElement;
            var focused = focusedInput as DependencyObject;
            if (focusedInput == null)
            {
                return false;
            }

            var request = new TraversalRequest(direction);
            var moved = false;
            if (focusedInput is UIElement uiElement)
            {
                moved = uiElement.MoveFocus(request);
            }
            else if (focusedInput is ContentElement contentElement)
            {
                moved = contentElement.MoveFocus(request);
            }

            if (!moved)
            {
                return false;
            }

            var next = Keyboard.FocusedElement as DependencyObject;
            if (next == null || ReferenceEquals(next, focused))
            {
                return false;
            }

            if (containmentRoot == null ||
                ReferenceEquals(next, containmentRoot) ||
                IsDescendantOf(next, containmentRoot))
            {
                return true;
            }

            if (focusedInput is UIElement previousElement)
            {
                FocusElement(previousElement);
            }

            return false;
        }

        internal static bool IsKeyboardFocusWithin(DependencyObject root)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            return root != null &&
                   focused != null &&
                   (ReferenceEquals(focused, root) || IsDescendantOf(focused, root));
        }

        internal static bool IsFocusWithinDataGridColumnHeader(DataGrid grid)
        {
            return GetFocusedDataGridColumnHeader(grid) != null;
        }

        internal static DataGridColumnHeader GetFocusedDataGridColumnHeader(DataGrid grid)
        {
            if (grid == null)
            {
                return null;
            }

            var header = FindAncestor<DataGridColumnHeader>(Keyboard.FocusedElement as DependencyObject);
            if (header?.Column == null ||
                !IsDescendantOf(header, grid))
            {
                return null;
            }

            return header;
        }

        internal static DataGridRow GetTargetDataGridRow(DataGrid grid)
        {
            if (grid == null)
            {
                return null;
            }

            var focusedRow = FindAncestor<DataGridRow>(Keyboard.FocusedElement as DependencyObject);
            if (focusedRow != null &&
                ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(focusedRow), grid))
            {
                return focusedRow;
            }

            var index = grid.SelectedIndex;
            if (index < 0 && grid.Items.Count > 0)
            {
                index = 0;
                grid.SelectedIndex = index;
            }

            if (index < 0)
            {
                return null;
            }

            grid.UpdateLayout();
            grid.ScrollIntoView(grid.Items[index]);
            grid.UpdateLayout();
            return grid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow;
        }

        internal static bool FocusElement(UIElement element)
        {
            if (element == null || !element.IsEnabled || !element.IsVisible)
            {
                return false;
            }

            element.Focusable = true;
            element.Focus();
            Keyboard.Focus(element);

            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.BringIntoView();
            }

            return element.IsKeyboardFocusWithin || ReferenceEquals(Keyboard.FocusedElement, element);
        }

        internal static bool FocusFirstElement(params UIElement[] elements)
        {
            return FocusFirstElement((IEnumerable<UIElement>)elements);
        }

        internal static bool FocusFirstElement(IEnumerable<UIElement> elements)
        {
            if (elements == null)
            {
                return false;
            }

            foreach (var element in elements)
            {
                if (FocusControllerElement(element))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool FocusElementByDelta(IList<UIElement> elements, int delta, bool wrap = false)
        {
            if (elements == null || elements.Count == 0 || delta == 0)
            {
                return false;
            }

            var focused = Keyboard.FocusedElement as DependencyObject;
            var currentIndex = FindFocusedElementIndex(elements, focused);
            var nextIndex = currentIndex < 0
                ? (delta > 0 ? 0 : elements.Count - 1)
                : currentIndex + delta;

            if (wrap)
            {
                nextIndex = (nextIndex + elements.Count) % elements.Count;
            }
            else
            {
                if (nextIndex < 0 || nextIndex >= elements.Count || nextIndex == currentIndex)
                {
                    return false;
                }
            }

            var step = delta > 0 ? 1 : -1;
            var attempts = 0;
            while (nextIndex >= 0 && nextIndex < elements.Count && attempts < elements.Count)
            {
                attempts++;
                if (FocusControllerElement(elements[nextIndex]))
                {
                    return true;
                }

                nextIndex += step;
                if (wrap)
                {
                    nextIndex = (nextIndex + elements.Count) % elements.Count;
                    if (currentIndex >= 0 && nextIndex == currentIndex)
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        internal static List<UIElement> GetVisibleFocusableElements(DependencyObject root)
        {
            return EnumerateVisualDescendants<UIElement>(root)
                .Where(IsControllerFocusable)
                .Distinct()
                .ToList();
        }

        internal static List<T> GetVisibleDescendantElements<T>(DependencyObject root)
            where T : UIElement
        {
            return EnumerateVisualDescendants<T>(root)
                .Where(element => element.IsVisible && element.IsEnabled)
                .Distinct()
                .ToList();
        }

        internal static bool ActivateFocusedElement()
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused == null)
            {
                return false;
            }

            if (ActivateElement(focused))
            {
                return true;
            }

            // If the focused element itself isn't a button/toggle, look for one inside it (useful for DataGrid rows/cells)
            var interactive = EnumerateVisualDescendants<UIElement>(focused)
                .FirstOrDefault(e => e.IsVisible && e.IsEnabled && (e is ButtonBase || e is Selector || e is DatePicker || e is Expander));

            if (interactive != null)
            {
                return ActivateElement(interactive);
            }

            // If we're on a DataGrid cell or row and no button was found, try a generic toggle/reveal action
            if (focused is FrameworkElement frameworkElement)
            {
                var dataContext = frameworkElement.DataContext;
                if (dataContext is AchievementDisplayItem displayItem)
                {
                    displayItem.ToggleReveal();
                    return true;
                }
                
                if (dataContext is ManualAchievementEditItem manualItem)
                {
                    manualItem.ToggleReveal();
                    return true;
                }
            }

            return false;
        }

        internal static bool ActivateElement(DependencyObject focused)
        {
            if (focused == null)
            {
                return false;
            }

            var comboBox = FindAncestor<ComboBox>(focused) ?? focused as ComboBox;
            if (comboBox != null)
            {
                comboBox.IsDropDownOpen = !comboBox.IsDropDownOpen;
                return true;
            }

            var datePicker = FindAncestor<DatePicker>(focused) ?? focused as DatePicker;
            if (datePicker != null)
            {
                datePicker.IsDropDownOpen = true;
                return true;
            }

            var expander = FindAncestor<Expander>(focused) ?? focused as Expander;
            if (expander != null)
            {
                expander.IsExpanded = !expander.IsExpanded;
                return true;
            }

            var button = FindAncestor<ButtonBase>(focused) ?? focused as ButtonBase;
            return button != null && ActivateButtonBase(button);
        }

        internal static bool OpenContextMenu(FrameworkElement owner, ContextMenu menu)
        {
            if (owner == null ||
                menu == null ||
                menu.Items.Count == 0 ||
                !owner.IsEnabled ||
                owner.Visibility != Visibility.Visible)
            {
                return false;
            }

            RoutedEventHandler closedHandler = null;
            closedHandler = (_, __) =>
            {
                menu.Closed -= closedHandler;
                owner.Dispatcher.BeginInvoke(new Action(() => FocusElement(owner)), DispatcherPriority.Input);
            };

            menu.Closed += closedHandler;
            menu.PlacementTarget = owner;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.IsOpen = true;
            FocusFirstContextMenuItem(menu);
            return true;
        }

        private static bool TryRouteControllerInputToContextMenu(ContextMenu menu, ControllerInput input)
        {
            if (menu == null || !menu.IsOpen)
            {
                return false;
            }

            Key key;
            if (TryGetVerticalDelta(input, out var verticalDelta))
            {
                EnsureContextMenuFocus(menu);
                return true;
            }
            else if (IsAcceptInput(input))
            {
                key = Key.Enter;
            }
            else if (IsBackInput(input))
            {
                key = Key.Escape;
            }
            else
            {
                return false;
            }

            var target = Keyboard.FocusedElement as DependencyObject;
            if (FindAncestor<ContextMenu>(target) != menu)
            {
                var first = GetFocusableMenuItems(menu).FirstOrDefault();
                if (first != null)
                {
                    FocusElement(first);
                    target = first;
                }
                else
                {
                    target = menu;
                }
            }

            return RaiseWpfKey(menu, target, key);
        }

        private static void EnsureContextMenuFocus(ContextMenu menu)
        {
            var target = Keyboard.FocusedElement as DependencyObject;
            if (FindAncestor<ContextMenu>(target) == menu)
            {
                return;
            }

            var first = GetFocusableMenuItems(menu).FirstOrDefault();
            if (first != null)
            {
                FocusElement(first);
            }
        }

        private static bool RaiseWpfKey(ContextMenu menu, DependencyObject target, Key key)
        {
            var element = target as UIElement ?? menu;
            var source = PresentationSource.FromVisual(element) ?? PresentationSource.FromVisual(menu);
            if (source == null)
            {
                if (key == Key.Escape)
                {
                    menu.IsOpen = false;
                    return true;
                }

                return false;
            }

            var keyArgs = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            element.RaiseEvent(keyArgs);

            if (key == Key.Escape && !keyArgs.Handled && menu.IsOpen)
            {
                menu.IsOpen = false;
            }

            return true;
        }

        internal static T FindAncestor<T>(DependencyObject current)
            where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T typed)
                {
                    return typed;
                }

                current = GetParent(current);
            }

            return null;
        }

        internal static bool IsDescendantOf(DependencyObject current, DependencyObject ancestor)
        {
            if (current == null || ancestor == null)
            {
                return false;
            }

            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }

                current = GetParent(current);
            }

            return false;
        }

        private static bool ActivateButtonBase(ButtonBase button)
        {
            if (button == null || !button.IsEnabled || button.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (button is RadioButton radioButton)
            {
                radioButton.IsChecked = true;
            }
            else if (button is ToggleButton toggleButton)
            {
                toggleButton.IsChecked = !(toggleButton.IsChecked ?? false);
            }

            ExecuteCommand(button.Command, button.CommandParameter, button.CommandTarget);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

            if (button is Button plainButton &&
                plainButton.ContextMenu?.IsOpen == true)
            {
                FocusFirstContextMenuItem(plainButton.ContextMenu);
            }

            return true;
        }

        private static void ExecuteCommand(ICommand command, object parameter, IInputElement target)
        {
            if (command == null)
            {
                return;
            }

            if (command is RoutedCommand routedCommand)
            {
                if (routedCommand.CanExecute(parameter, target))
                {
                    routedCommand.Execute(parameter, target);
                }

                return;
            }

            if (command.CanExecute(parameter))
            {
                command.Execute(parameter);
            }
        }

        private static ContextMenu FindOpenContextMenuForWindow(Window window)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            var focusedMenu = FindAncestor<ContextMenu>(focused);
            if (focusedMenu?.IsOpen == true &&
                IsPlacementTargetInsideWindow(focusedMenu.PlacementTarget, window))
            {
                return focusedMenu;
            }

            return FindOpenContextMenuInTree(window);
        }

        private static ContextMenu FindOpenContextMenuInTree(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            if (root is FrameworkElement element &&
                element.ContextMenu?.IsOpen == true)
            {
                return element.ContextMenu;
            }

            var childCount = GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                var nested = FindOpenContextMenuInTree(VisualTreeHelper.GetChild(root, i));
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static void FocusFirstContextMenuItem(ContextMenu menu)
        {
            if (menu == null)
            {
                return;
            }

            menu.Dispatcher.BeginInvoke(new Action(() =>
            {
                var first = GetFocusableMenuItems(menu).FirstOrDefault();
                if (first != null)
                {
                    FocusElement(first);
                }
            }), DispatcherPriority.Input);
        }

        private static List<UIElement> GetFocusableMenuItems(ContextMenu menu)
        {
            if (menu == null)
            {
                return new List<UIElement>();
            }

            menu.UpdateLayout();
            var elements = new List<UIElement>();
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                AddFocusableContextMenuElements(item, elements);
            }

            return elements;
        }

        private static void AddFocusableContextMenuElements(MenuItem item, List<UIElement> elements)
        {
            if (item == null ||
                elements == null ||
                !item.IsEnabled ||
                item.Visibility != Visibility.Visible)
            {
                return;
            }

            if (item.Header is DependencyObject header)
            {
                foreach (var button in EnumerateVisualDescendants<ButtonBase>(header)
                             .Where(button => button.IsEnabled && button.Visibility == Visibility.Visible))
                {
                    elements.Add(button);
                }
            }

            var localFocusable = item.ReadLocalValue(UIElement.FocusableProperty);
            if (!(localFocusable is bool isFocusable && !isFocusable))
            {
                elements.Add(item);
            }
        }

        private static int FindFocusedElementIndex(IList<UIElement> elements, DependencyObject focused)
        {
            if (elements == null || focused == null)
            {
                return -1;
            }

            for (var i = 0; i < elements.Count; i++)
            {
                var element = elements[i];
                if (ReferenceEquals(element, focused) || IsDescendantOf(focused, element))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsControllerFocusable(UIElement element)
        {
            if (element == null || !element.Focusable || !element.IsEnabled || !element.IsVisible)
            {
                return false;
            }

            if (element is ScrollViewer || element is ScrollBar || element is Thumb)
            {
                return false;
            }

            return element is ButtonBase ||
                   element is TextBoxBase ||
                   element is ComboBox ||
                   element is DatePicker ||
                   element is Expander ||
                   element is DataGrid ||
                   element is DataGridRow ||
                   element is ListBoxItem;
        }

        private static bool FocusControllerElement(UIElement element)
        {
            if (element is DataGrid grid)
            {
                return FocusDataGrid(grid);
            }

            return FocusElement(element);
        }

        private static bool UsesControllerCellFocus(DataGrid grid)
        {
            return string.Equals(grid?.Tag as string, CellFocusTag, StringComparison.Ordinal);
        }

        private static bool FocusDataGridCell(DataGrid grid, DataGridRow row, int itemIndex)
        {
            if (grid == null || row == null || itemIndex < 0 || itemIndex >= grid.Items.Count)
            {
                return false;
            }

            var column = grid.Columns
                .Where(candidate => candidate != null && candidate.Visibility == Visibility.Visible)
                .OrderBy(candidate => candidate.DisplayIndex)
                .FirstOrDefault();
            if (column == null)
            {
                return false;
            }

            grid.CurrentCell = new DataGridCellInfo(grid.Items[itemIndex], column);
            grid.ScrollIntoView(grid.Items[itemIndex], column);
            grid.UpdateLayout();

            var cell = EnumerateVisualDescendants<DataGridCell>(row)
                .FirstOrDefault(candidate => ReferenceEquals(candidate.Column, column));
            return cell != null && FocusElement(cell);
        }

        private static List<DataGridColumnHeader> GetVisibleDataGridColumnHeaders(DataGrid grid)
        {
            if (grid == null)
            {
                return new List<DataGridColumnHeader>();
            }

            grid.UpdateLayout();
            return EnumerateVisualDescendants<DataGridColumnHeader>(grid)
                .Where(header =>
                    header.Column != null &&
                    header.Column.Visibility == Visibility.Visible &&
                    header.IsEnabled &&
                    header.Visibility == Visibility.Visible)
                .OrderBy(header => header.Column.DisplayIndex)
                .ToList();
        }

        private static DataGridColumn GetFocusedDataGridColumn(DataGrid grid)
        {
            if (grid == null)
            {
                return null;
            }

            var focused = Keyboard.FocusedElement as DependencyObject;
            var header = GetFocusedDataGridColumnHeader(grid);
            if (header?.Column != null)
            {
                return header.Column;
            }

            var cell = FindAncestor<DataGridCell>(focused);
            if (cell != null && IsDescendantOf(cell, grid))
            {
                return cell.Column;
            }

            var currentColumn = grid.CurrentCell.Column;
            return currentColumn?.Visibility == Visibility.Visible ? currentColumn : null;
        }

        private static bool IsFirstVisibleColumn(DataGrid grid, DataGridColumn column)
        {
            if (grid == null || column == null || column.Visibility != Visibility.Visible)
            {
                return false;
            }

            var firstColumn = grid.Columns
                .Where(candidate => candidate != null && candidate.Visibility == Visibility.Visible)
                .OrderBy(candidate => candidate.DisplayIndex)
                .FirstOrDefault();
            return ReferenceEquals(firstColumn, column);
        }

        private static T FindVisualDescendant<T>(DependencyObject root)
            where T : DependencyObject
        {
            return EnumerateVisualDescendants<T>(root).FirstOrDefault();
        }

        private static void PrepareWindowForControllerFocus(Window window)
        {
            foreach (var button in EnumerateVisualDescendants<ButtonBase>(window))
            {
                if (button.ContextMenu != null)
                {
                    button.Focusable = true;
                }
            }
        }

        private static void EnsureFocusInsideWindow(Window window)
        {
            if (window == null || !window.IsActive || window.IsKeyboardFocusWithin)
            {
                return;
            }

            var target = GetVisibleFocusableElements(window).FirstOrDefault();
            if (target != null)
            {
                FocusElement(target);
            }
        }

        private static IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
            {
                yield break;
            }

            var childCount = GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed)
                {
                    yield return typed;
                }

                foreach (var nested in EnumerateVisualDescendants<T>(child))
                {
                    yield return nested;
                }
            }
        }

        private static int GetChildrenCount(DependencyObject root)
        {
            if (root == null || !(root is Visual || root is Visual3D))
            {
                return 0;
            }

            return VisualTreeHelper.GetChildrenCount(root);
        }

        private static DependencyObject GetParent(DependencyObject current)
        {
            if (current == null)
            {
                return null;
            }

            if (current is Visual || current is Visual3D)
            {
                var visualParent = VisualTreeHelper.GetParent(current);
                if (visualParent != null)
                {
                    return visualParent;
                }
            }

            if (current is FrameworkContentElement frameworkContentElement)
            {
                return frameworkContentElement.Parent ?? ContentOperations.GetParent(frameworkContentElement);
            }

            return LogicalTreeHelper.GetParent(current) ?? (current as FrameworkElement)?.Parent;
        }

        private static string NormalizeInputKey(ControllerInput input)
        {
            if (TryGetVerticalDelta(input, out var vertical))
            {
                return vertical < 0 ? "Up" : "Down";
            }

            if (TryGetHorizontalDelta(input, out var horizontal))
            {
                return horizontal < 0 ? "Left" : "Right";
            }

            return input.ToString();
        }

        private static bool IsDirectionalInputKey(string inputKey)
        {
            return string.Equals(inputKey, "Up", StringComparison.Ordinal) ||
                   string.Equals(inputKey, "Down", StringComparison.Ordinal) ||
                   string.Equals(inputKey, "Left", StringComparison.Ordinal) ||
                   string.Equals(inputKey, "Right", StringComparison.Ordinal);
        }

        private static bool IsDirectionalKey(Key key)
        {
            return key == Key.Up ||
                   key == Key.Down ||
                   key == Key.Left ||
                   key == Key.Right;
        }

        private struct HandledControllerInput
        {
            public HandledControllerInput(string inputKey, DateTime handledAtUtc)
            {
                InputKey = inputKey;
                HandledAtUtc = handledAtUtc;
            }

            public string InputKey { get; }

            public DateTime HandledAtUtc { get; }
        }
    }
}
