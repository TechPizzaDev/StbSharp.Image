# StbSharp.Image
[![Chat](https://img.shields.io/discord/628186029488340992.svg)](https://discord.gg/ZeHxhCY)

StbSharp.Image is a C# port of the stb_image.h, which is C library to load images in JPG, PNG, BMP, TGA, PSD and GIF formats.

This fork contains massive changes and is not production-ready. Use at your own risk.

It is important to note, that this project is **port** (not **wrapper**).  
Original C code had been ported to C#. Therefore native binaries are not required.

The porting was based with [Sichem](https://github.com/rds1983/Sichem), which is a C to C# code converter utility,  
and later optimized and cleaned up by hand.

# Adding Reference
    a. `git submodule add https://github.com/StbSharp/StbImageSharp.git`
    
    b. Add src/StbImageSharp.csproj to the solution
     
# Usage
StbSharp.Image exposes API similar to stb_image.h, but has changed the API considerably in favor of safety.  
For more possible use cases check out the [original repository](https://github.com/StbSharp/StbImageSharp).

# License
Public Domain

# Credits
* [stb](https://github.com/nothings/stb) library
* [rds](https://github.com/rds1983)
