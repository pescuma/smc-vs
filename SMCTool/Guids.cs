// Guids.cs
// MUST match guids.h
using System;

namespace RicardoPescumaDomenecci.SMCTool
{
    static class GuidList
    {
        public const string guidSMCToolPkgString = "3a21f2e7-a518-4f32-9bff-099168731b6e";
        public const string guidSMCToolCmdSetString = "89b8d8b8-9ca1-4d40-b132-7318d1d0bff6";

        public static readonly Guid guidSMCToolCmdSet = new Guid(guidSMCToolCmdSetString);
    };
}