# Changelog

## 0.6.2

1. The LOG panel now shows the progress of an app update download.
2. Removed the "Skip this update" option. The app now offers an available update at every startup.
3. Added a link to the program's website in Settings > About.
4. Prepared the app for publishing in the Microsoft Store and winget.

## 0.6.1

1. Fixed crashes when exporting telemetry or opening the performance test while viewing a GPS track without a recording.
2. Added a one-time invitation to the [Telemavix GPS closed alpha testing](https://sefinek.net/projects/telemavix/closed-alpha-testing).
3. Updated dependencies: Avalonia 12.2.0 and SkiaSharp 4.158.0.

## 0.6.0

1. Improved the appearance of dialog windows.
2. The "Rendering complete" dialog now includes more information.
3. Added support for DJI Lito X1 Plus.
4. Added support for Garmin 7X.
5. Created Telemavix GPS, a mobile app for recording routes and phone sensor data for use in Studio. [Join the closed alpha testing](https://sefinek.net/projects/telemavix/closed-alpha-testing) to try it and share your feedback.
6. Added optional error reporting (BugSnag). It can be disabled in Settings > Privacy.
7. The route overview card for drones now shows height above the takeoff point instead of altitude above sea level.
8. Updated dependencies: Avalonia 12.1.6, SkiaSharp 4.156.1, and NuGet.Versioning 8.0.0.
9. Reorganized the settings to make them easier to use.
10. A track from a phone that lost its GPS for minutes is now shown as lost GPS instead of a straight line between the last and the next fix.
11. Many other improvements and fixes.
