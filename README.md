# N.I.N.A. Fujifilm Native Plugin

![N.I.N.A.](https://img.shields.io/badge/N.I.N.A.-3.0%2B-purple?style=flat-square)
![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue?style=flat-square)
![License](https://img.shields.io/badge/License-Apache_2.0-green?style=flat-square)
![X-Trans](https://img.shields.io/badge/Sensor-X--Trans_Ready-red?style=flat-square)

Unlock the full potential of your Fujifilm camera in [N.I.N.A. (Nighttime Imaging 'N' Astronomy)](https://nighttime-imaging.eu/). This plugin bypasses generic ASCOM drivers to communicate directly with your camera's firmware via USB, offering features that were previously impossible.

---

## 🚀 Key Features

### 🎨 X-Trans Color Preview (New!)
*   **Synthetic Bayer Technology**: This plugin implements a novel solution for X-Trans live view. It intelligently processes X-Trans data to provide a **Full Color Live Preview** directly in N.I.N.A.
    *   *Eliminates black & white mosaics.*
    *   *Resolves "zoomed in" artifacts.*
    *   *Provides a clean, color image for framing and focus.*
*   **Non-Destructive**: Saved images are still the original, bit-perfect `.RAF` files, preserving the full raw sensor data for post-processing.

### 📸 Native Camera Control
*   **Direct USB Connection**: Faster and more stable than ASCOM.
*   **Robust Exposure Control**: Full support for **ISO**, **Shutter Speed**, and **Bulb Mode**.
    *   *Automatically handles the transition to Bulb for exposures > 30s.*
*   **Smart Metadata**: Automatically writes the correct `BAYERPAT` and `ROWORDER` to FITS headers, ensuring images stack correctly in PixInsight, Siril, and DeepSkyStacker.

### 🔭 Electronic Lens Focuser
*   **Turn your Lens into an Autofocuser**: The plugin exposes your attached electronic lens as a **Focuser Device** in N.I.N.A.
*   **Autofocus Ready**: Fully compatible with N.I.N.A.'s advanced autofocus routines (Hocus Focus, Overshoot).
    *   *Perfect for wide-field rigs using native Fuji glass.*

---

## 📷 Supported Models

The plugin utilizes the official Fujifilm SDK and `LibRaw` for broad compatibility.

| Series | Verified Models |
| :--- | :--- |
| **GFX System** | GFX 100 II, GFX 100S, GFX 100, GFX 50S II, GFX 50R |
| **X-H Series** | X-H2S, X-H2, X-H1 |
| **X-T Series** | X-T5, X-T4, X-T3, X-T2 |
| **X-S Series** | X-S20, X-S10 |
| **Other** | X-Pro3, X-M5 |

*> **Note:** Older models (e.g., X-T1, X-E2) generally lack the USB tethering protocol required for this plugin.*

---

## ⚙️ Installation & Setup

### Prerequisites
*   **N.I.N.A. 3.0+** (Beta/Nightly builds recommended).
*   **Visual C++ Redistributable (x64)**.

### Installation
1.  Download the latest release zip from the [**Releases**](../../releases) page.
2.  Navigate to your local N.I.N.A. plugins directory:
    ```text
    %LOCALAPPDATA%\NINA\Plugins\
    ```
3.  Create a new folder named `Fujifilm`.
4.  Extract the contents of the zip file into this folder.
5.  Restart N.I.N.A.

### Camera Settings (Crucial!)
For the plugin to control the camera correctly, set your physical dials as follows:
1.  **Connection Mode**: `USB TETHER SHOOTING AUTO` or `PC SHOOT AUTO`.
2.  **Drive Dial**: `S` (Single Shot).
3.  **Shutter Dial**: `T` (Time) or `A` (Auto) - *This allows software control of shutter speed.*
4.  **ISO Dial**: `A` (Auto) or `C` (Command) - *This allows software control of ISO.*
5.  **Focus Mode**:
    *   **S** or **C** (AF) -> To use the **Lens Focuser** feature.
    *   **M** (Manual) -> To use a telescope/manual focus.

### N.I.N.A. Settings for Color Preview
To see the Color Preview for X-Trans cameras:
1.  Go to **Options -> Imaging**.
2.  Ensure **Debayer Image** (or "Auto Debayer") is turned **ON**.
3.  In the Imaging tab, ensure the **Debayer** button (top right of the image panel) is active.

---

## 🐛 Troubleshooting

| Issue | Solution |
| :--- | :--- |
| **"Camera Busy" / Exposure Fail** | The camera is likely writing to the SD card. Increase the "Image Download Delay" in N.I.N.A. options. |
| **Exposure Error 0x2003** | This means "Combination Error". Ensure your physical Shutter/ISO dials are set to **T/A** or **C** to allow software control. |
| **Black & White Preview** | Ensure **Debayer Image** is enabled in N.I.N.A. options. The plugin sends a "Synthetic Bayer" image that requires debayering to show color. |
| **Focus Timeout** | Ensure the lens clutch is not pulled back (Manual Mode) on lenses like the 16mm f/1.4 or 23mm f/1.4. |

---

## 🧑‍💻 Development

To build from source:
1.  **Visual Studio 2022**.
2.  **.NET 7.0/8.0 SDK**.
3.  **Fujifilm SDK**: You must apply for the SDK from Fujifilm and place the `.dll` and `.lib` files in `src/NINA.Plugins.Fujifilm/Interop/Native`.

```powershell
dotnet build -c Release
```

---

## 📄 License

Distributed under the **Apache 2.0 License**. See `LICENSE` for more information.

*Disclaimer: This software is an independent community project and is not affiliated with, endorsed by, or associated with FUJIFILM Corporation.*
