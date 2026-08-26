# The Fluent interface (WPF-UI)

The .NET 8 upgrade moved the libraries forward **without touching the UI**: the views were still the
2019 ones — flat tabs, `Margin="16"` written by hand on every element, `Label` instead of
`TextBlock`, black text boxes with green text, no dark theme. This step rebuilds the interface.

## Change of library

| Package | Before | After |
|---|---|---|
| MahApps.Metro | 2.4.11 | **removed** |
| MaterialDesignThemes.MahApps | 5.3.2 | **removed** |
| WPF-UI | — | **4.3.0** |
| Extended.Wpf.Toolkit | 5.1.2 | 5.1.2 (kept) |

**Why WPF-UI.** It is the Fluent / WinUI 3 implementation for WPF: Mica backdrop, `NavigationView`,
`FluentWindow`, Windows 11 control templates, and a theme manager that switches light and dark at
runtime. Material Design and MahApps were doing two overlapping jobs — two theme sets, two palettes,
two families of controls — and neither looked like the operating system the application runs on.

**Why Extended.Wpf.Toolkit stays.** WPF-UI has no equivalent of `TimeSpanUpDown` and `DateTimePicker`,
which serve the PLC types `TIME`, `LTIME` and `DATE_AND_TIME`. The other Xceed numeric controls
(`IntegerUpDown`, `ByteUpDown`, `SingleUpDown`, `DoubleUpDown`) moved to `ui:NumberBox`. The two
survivors get the theme's tints through the `XceedInput` style in `Themes/Tokens.xaml`; without it
they would stay two pale grey boxes in a dark window.

## Structure

```
Themes/Tokens.xaml                       spacing, radii, typography, shared styles, common converters
Themes/AppTheme.cs                       light/dark switch and persistence of the preference
Views/Pages/*.xaml                       4 Pages hosting the views inside the NavigationView
Views/Pages/NavigationPageProvider.cs    binds each Page to its view model
```

### From TabControl to NavigationView

`TabsView` is gone: the four functions are now entries in a side `ui:NavigationView`, each with its
own icon.

The delicate point is the data context. A `Page` navigated inside a `Frame` **does not inherit** the
window's `DataContext`, and `ViewModelLocator.MainWindowViewModel` calls `Kernel.Get<>` on every
access — using it from the pages would produce a second set of view models, never initialised, that
do not talk to the PLC. `NavigationPageProvider` therefore implements `INavigationViewPageProvider`
and returns each page with **the** view model already built by `TabsViewModel` at startup.

`MainWindow.OnLoaded` wires the provider and navigates to the first page: the `NavigationView` does
not navigate by itself at startup, so there is no race between two navigations.

### Theme

Dark by default, with a toggle in the footer of the navigation panel. The choice is saved in
`%LOCALAPPDATA%\TwinCatAdsTool\theme.txt` — not next to the executable, which on a cabinet PC is
usually not writable.

No colour is written as a literal in the views: they are all `DynamicResource` lookups into the
WPF-UI dictionary, which is what lets a theme change propagate without a restart. The exceptions are
declared and justified: the diff colours in `CompareView` and the plot colours in `GraphViewModel`,
which do not go through the WPF resource system.

**OxyPlot** knows nothing about the theme: `GraphViewModel.ApplyPlotTheme()` repaints axes, legend
and text, and is hooked to `ApplicationThemeManager.Changed` — with a `-=` in `Dispose`, or the
static event would keep the view model alive.

## Small features added

### A real progress bar

`IPersistentVariableService.CurrentTask` was an `IObservable<string>`. A sentence like
`"Reading 40-60 of 480..."` cannot drive a progress bar without parsing the numbers back out of the
text. It is now an `IObservable<OperationProgress>`, with `Done`, `Total` and `Message` side by side.

### Outcome as an info bar

`LastReportSummary` was a bold `Label` next to the buttons. It is now a `ui:InfoBar` with a severity:
green when the backup or restore is complete, amber otherwise. `IsOpen` is bound `TwoWay` on
purpose — the property is not two-way by default, and with a one-way binding, dismissing the bar by
hand would remove it **for good**.

### Compare: how many lines differ

`DifferenceCount` counts the lines that are not `Unchanged`, shown as a badge above the two panes.
Finding out whether two backups are identical should not require scrolling through thousands of
lines.

### Compare: the rendering, twice

The diff colours were opaque and designed for the light theme (`Colors.White` for unchanged lines,
solid yellow for inserted ones): on a dark background they would have been unreadable. They are now
translucent fills plus a solid bar on the left edge, so the distinction does not rest on the
background colour alone.

Two defects then showed on Windows, with a common root: **the view model was building `ListBoxItem`
controls with their colours already set** and handing them to a `ListView`. A `ListBoxItem` is not
that list's own container type, so each one was wrapped in a `ListViewItem` whose style decided the
row's width, padding and background — the colour was there but never reached the eye. And the list
sat inside an outer `ScrollViewer`, which gave it infinite height and **disabled virtualization**:
with a real plant's backup, more than ten thousand rows were realised at once.

The view model now exposes plain `DiffLine` values (text plus kind). The colours live in a
`DataTemplate`, on a `Border` stretched across the full row width, and the container draws nothing
of its own that could cover them. The list virtualises and **scrolls by line rather than by pixel**,
which also keeps the two panes exactly aligned, since every row is 20 high.

Three further points, each a defect in its own right:

- WPF-UI's list template uses a **`PassiveScrollViewer`**, which by design does not act on the mouse
  wheel and lets it bubble to whatever is above. The wheel worked only while the pointer was over
  the scrollbar. The list now has a `ControlTemplate` of its own with a plain `ScrollViewer`. The
  original code had a workaround for this — an emptied template inside an outer `ScrollViewer` — but
  that was what disabled virtualization.
- The two panes were synchronised **without a re-entrancy guard**. The pane being followed reports
  the offset it actually reached, clamped to its own extent, and the other scrolled back to that
  clamped value: the two chased each other.
- WPF-UI's scrollbars are a few pixels wide and only widen once the pointer is on them — too fine a
  target for a pane that is scrolled constantly. The panes now use scrollbars of their own, 16 wide,
  still theme-aware through `DynamicResource`. The implicit style is declared in the `Resources` of
  the `ScrollViewer` itself, because an implicit style further out is not guaranteed to reach across
  a template boundary.

### Restore: what is about to be written

The name of the loaded file and the number of variables, next to the buttons, before pressing
*Write*.

### Restore: virtualization

The list had `ScrollViewer.CanContentScroll="False"`, which **disables virtualization**: with a large
backup every row was materialised. It now uses `ScrollUnit="Pixel"`, which keeps virtualization even
with rows of differing height.

## Removed

- `Views/TabsView.xaml` and its code-behind, replaced by the `NavigationView`.
- `Converters/ConnectionStateToIconConverter.cs` — no consumer left after the connection bar was
  rewritten to use a coloured dot and the state name instead of an icon.

## Deliberately not touched

**The `MessageBox` calls.** They are still the system ones (`System.Windows.MessageBox`): 14 calls
spread across the view models, some from synchronous methods. `ui:MessageBox` is an asynchronous
window (`ShowDialogAsync`), so converting them would change the shape of those methods — including
the "do you want to overwrite the variables on the PLC?" confirmation, which is the point where a
mistake costs most. It deserves a separate step, verified on Windows.

## Verification

`dotnet build` and `dotnet publish -r win-x64` pass from macOS without warnings
(`EnableWindowsTargeting`, see `UPGRADE-NET8.md`). Two static audits run over the XAML: one for
bindings that write to properties without a setter — the class of error that once stopped the
application from starting — and one for `StaticResource` keys that are not defined anywhere.

**Appearance and runtime behaviour cannot be verified without Windows.** The window, the dark theme,
the side navigation, the title bar and the error info bar were confirmed on a real machine; the Mica
backdrop, switching the theme with the window open, and the `NumberBox` controls on integer PLC
types are still to be checked in the field.
