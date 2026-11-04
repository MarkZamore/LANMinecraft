# Measure a launcher notice

Run `dotnet run --project tools/MeasureNotice -- <messages.json> <output-directory>`.
Keep input/output under the ignored repository `artifacts` directory.

Input is a JSON array of `{ "Name": "memory", "Heading": "...", "Detail": "..." }`.
Names contain only lowercase ASCII letters, digits and hyphens. The tool renders
the compiled `NoticeDialog` with App.xaml resources and installed system fonts,
at client widths 568/440/280 DIP and DPI 100/150/200%. It checks text, wrapping
width, font tokens and absence of a scaling transform, and exports PNGs and
`layout.json` for visual inspection.

The probe initializes application resources without running launcher startup.
It does not show windows, start Steam, send reports or launch Minecraft. PNGs
cover dialog content; they do not verify native window framing or keyboard input.
