# UVC Control (Avalonia)

Cross-platform port of the SwiftUI `UVCControl` app: inspect and adjust every UVC control a camera answers,
including controls it doesn't advertise and raw extension-unit controls. Built with .NET 10 and Avalonia 12.

## Layout

```
src/UVCControl.Core      UVC logic, no UI: descriptor parser, control catalog, device model, OS backends
src/UVCControl.App       Avalonia app (MVVM + DI + Generic Host), live preview (AVFoundation on macOS, FlashCap elsewhere)
tests/UVCControl.Core.Tests
scripts/build-macos-app.sh
```

### Architecture

- **Generic Host**: `Program.Main` builds a `HostApplicationBuilder` (configuration from `appsettings.json`,
  logging, DI), starts the host, then runs Avalonia with the host's `IServiceProvider`. `App` resolves
  `MainWindow` from the container.
- **DI**: `AddUvcControl()` (Core) registers `IUvcBackend` for the current OS and `IUvcDeviceManager`;
  `AddUvcControlApp()` registers the settings store, the capture service, ViewModels and windows.
  ViewModels that need a runtime argument are created through `IViewModelFactory` (`ActivatorUtilities`).
- **MVVM**: CommunityToolkit.Mvvm (`[ObservableProperty]` partial properties, `[RelayCommand]`), compiled
  bindings, one `DataTemplate` per control editor (sliders, toggle, menu, hold-to-move, step, hex).
- **Threading**: every device has a `SerialExecutor` thread; all USB I/O runs there in order, so the UI never
  blocks, and thread-affine handles (COM on Windows) stay on one thread.
- **Options**: the `Uvc` section of `appsettings.json` binds to `UvcOptions` (refresh delay after writes, live
  refresh interval).

### Platform backends

| OS | Control access | Notes |
|----|----------------|-------|
| macOS | IOKit `IOUSBDeviceInterface182` via P/Invoke, raw class requests on endpoint 0 | Same behavior as the Swift app, extension units included. |
| Linux | uvcvideo: `UVCIOC_CTRL_QUERY` for extension units, V4L2 controls for Camera Terminal / Processing Unit | Descriptors come from sysfs. The user needs access to `/dev/video*` (usually the `video` group). |
| Windows | DirectShow `IAMCameraControl` / `IAMVideoProcAmp` | Standard controls only; extension units are not exposed. Unit IDs are synthetic. |

Because Linux and Windows drivers own the device, the backend translates the app's raw UVC requests
(GET_INFO/MIN/MAX/RES/DEF/CUR, SET_CUR) to what the driver exposes.

## PTZ

The device page opens on the **PTZ** tab; the full control list lives under **Advanced**.

- **Motion** is always driven through the absolute controls (Pan/Tilt `0x0D`, Zoom `0x0B`, Focus `0x06`):
  `Core/Ptz/PtzMotionController` streams positions at `Ptz:TickRate` Hz, awaiting each SET_CUR so a slow camera
  drops ticks instead of queueing them. Relative controls aren't used.
  Positions are rounded to single units (1″ for pan/tilt), not to GET_RES: the Pocket 3 reports 1° but holds finer
  positions, and 1° steps make slow moves stutter. Set `Ptz:SnapToResolution` for cameras that need the reported step.
- **Joystick / hold buttons**: speed is proportional to deflection (radial dead zone, squared response) times the
  global *Jog* speed; 100 % is `MaxPanTiltDegreesPerSecond` for pan/tilt and `MaxZoomRangePerSecond` /
  `MaxFocusRangePerSecond` of the range for zoom/focus.
- **Presets** store only the axes you tick (Pan, Tilt, Zoom, Focus), per camera model (VID:PID), in
  `presets.json` next to the settings file. Recall uses a cubic ease-in/out; the global *Preset recall* speed sets
  the peak speed of the slowest axis and every axis arrives together. Recalling focus turns autofocus off.
- **Connections**: every camera is opened and probed as soon as it's found and stays connected, so switching is
  instant; only the selected camera runs a preview.

## Run

```sh
dotnet run --project src/UVCControl.App
dotnet run --project src/UVCControl.App -- --dump     # print every discovered control
dotnet test
scripts/build-macos-app.sh                            # build/UVC Control.app (needed for the camera permission prompt)
```

Set `Logging__LogLevel__UVCControl=Debug` to log every SET_CUR.

## Live preview

- **macOS**: `Services/MacOS/AVFoundationCaptureService` drives AVFoundation directly through the Objective-C
  runtime. It lists the camera's native formats (e.g. `420v`), asks for BGRA output so the system converts every
  format, and re-applies the chosen format after the session starts (starting resets it to the session preset).
  FlashCap isn't used on macOS: its AVFoundation backend over-releases `CMFormatDescription`s (crashing when a
  thread-pool thread exits), ignores the frame rate when picking a format, and mislabels pixel layouts.
- **Windows / Linux**: FlashCap (DirectShow / V4L2); raw formats arrive as BMP, MJPEG as JPEG.
- A format that delivers no frame within 4 s shows a message instead of a black preview. Cameras may advertise
  formats the OS can't decode: the DJI Osmo Pocket 3 only streams MJPEG (shown by macOS as `420v`) and H.264
  (shown as `2vuy`, which never delivers frames).
