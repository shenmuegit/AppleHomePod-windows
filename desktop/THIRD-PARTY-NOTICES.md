# Third-party components

AirFlash is dual-licensed under the GNU General Public License, version 3 or any later version (LICENSE-GPLv3), and the AirFlash Commercial License (LICENSE-COMMERCIAL.md; contact https://github.com/Ding-Kyoma/AirFlash/issues).

- .NET and WPF: Microsoft and contributors, MIT license. Runtime components are included by the official self-contained publishing pipeline.
- The Rust engine statically links the MSVC C runtime for standalone deployment.
- NAudio 2.2.1: Mark Heath and contributors, MIT license. NuGet packages include their license metadata.
- The Rust engine and its dependencies retain the notices and licenses described in native/airflash-engine/README.md and Cargo.lock.
- xUnit and Microsoft.NET.Test.Sdk are development-only and are not included in the published application.

The application icon and UI templates are project assets.

AppleHome modifications are distributed under GPL version 3 or later. The upstream commercial option does not grant a commercial license for these modifications.

VB-CABLE is a separately installed driver from VB-Audio Software. Its package and installer are not included, embedded, redistributed, or automatically run by AppleHome. Obtain the original package from https://vb-audio.com/Cable/ and follow the vendor's separate installation and licensing terms. The GPL application uses the installed Windows render endpoint through platform APIs; VB-CABLE is not an application library or bundled dependency.
