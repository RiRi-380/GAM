# Icon File Creation Instructions

The application requires a proper ICO file to set as the executable icon.

## Creating logo.ico from logo.png

You can use one of these methods:

### Method 1: Online Converter
1. Go to https://convertio.co/png-ico/ or similar online converter
2. Upload the logo.png file
3. Convert to ICO format with multiple sizes (16x16, 32x32, 48x48, 256x256)
4. Save as logo.ico in this directory

### Method 2: Using ImageMagick
If you have ImageMagick installed:
```bash
convert logo.png -define icon:auto-resize=256,48,32,16 logo.ico
```

### Method 3: Using GIMP
1. Open logo.png in GIMP
2. Export as "logo.ico"
3. Select multiple sizes when prompted

## After Creating the ICO File

Once you have a proper logo.ico file:

1. Replace the existing logo.ico in this directory
2. Uncomment the ApplicationIcon line in GmodAddonManager.UI.csproj:
   ```xml
   <ApplicationIcon>Assets\logo.ico</ApplicationIcon>
   ```
3. Rebuild the project

The icon will then appear:
- As the executable file icon
- In the Windows taskbar
- In the window title bar (already configured)