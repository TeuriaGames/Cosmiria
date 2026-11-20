#!/usr/bin/python
# Compiles src/Shaders/*.slang into Compiled/Vulkan (*.spv) and Compiled/DX12 (*.dxil).
# DXIL needs dxcompiler available to slangc; failures there are reported but not fatal.
import os
import subprocess

os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Shaders"))

for shader in sorted(os.listdir(".")):
    filename, ext = os.path.splitext(shader)
    if ext != ".slang":
        continue

    print("Compiling " + shader)
    vk_out = "Compiled/Vulkan/" + filename + ".spv"
    dx_out = "Compiled/DX12/" + filename + ".dxil"

    if subprocess.call(["slangc", shader, "-target", "spirv", "-emit-spirv-via-glsl", "-O3", "-o", vk_out, "-entry", "main"]) != 0:
        raise SystemExit("SPIR-V compilation failed for " + shader)

    if subprocess.call(["slangc", shader, "-profile", "sm_6_3", "-target", "dxil", "-O3", "-o", dx_out, "-entry", "main"]) != 0:
        print("  (skipped DXIL for " + shader + ")")
