"""Bring the public HLSLcc (2020) Metal output in line with what Unity 2022.3's player expects.

Usage: python3 -I patch_hlslcc.py <copy of HLSLcc source tree>

Each change mirrors what Unity 2022.3's own Metal shaders contain:
1. Colour outputs use the render-pass remap function constant:
   `rp_output_remap_mask [[ function_constant(1) ]]`, 4 bits per output.
   This replaces the old `xlt_remap_o` array.
2. Vertex positions are `[[ position, invariant ]]`. HDRP's depth prepass followed by ZTest Equal relies on this.
3. Base vertex/instance are subtracted only when the player sets `has_base_vertex_instance`
   (`function_constant(4)`).
4. Multisampled texture arrays are read as `tex.read(coord.xy, coord.z, sample)`.
5. SV_PrimitiveID in pixel shaders becomes `[[ primitive_id ]]`. Unity's Metal build compiles HDRP's
   debug-display variants without it; ours come from the D3D variant, which reads it.
6. Tessellation (vertex + hull + domain translated through one GLSLCrossDependencyData, docs/METAL-TESSELLATION.md):
   - the tessellation `vertexFunction` declares `has_base_vertex_instance` too (it subtracts the bases);
   - the post-tessellation function also takes `patch_id`, the control point / patch constant / tess factor
     buffers after its own buffers, and reads `tessFactor = tessFactors[patchID]`;
   - the domain stage's SV_TessFactor / SV_InsideTessFactor inputs read that `tessFactor`, as in the hull;
   - scalar control point inputs lose their `.x` where Unity's do (domain; hull with a control point phase).
7. Stencil reads take the first channel. A D3D stencil view (X24_G8 / X32_G8X24) has the stencil in .y, and
   the D3D bytecode reads .y; Unity's Metal stencil view has it in .x, and Unity's own Metal shaders read .x
   (Core RP's GetStencilValue). Applies to uint textures whose name contains "Stencil" (HDRP's _StencilTexture).
8. A mov between 16-bit integer registers (min16uint, UI Toolkit's Hidden/Internal-UIRDefault) assigns the 32-bit
   value TranslateOperand gives (`ushort2 = uint2(...)`, which MSL rejects); convert it to the destination type.
9. bfi with a scalar operand (a cbuffer scalar, InstanceOcclusionCullingKernels) and a vector destination: expand
   every operand to the destination width, so the bitFieldInsert template gets one type.
"""
import pathlib
import sys

root = pathlib.Path(sys.argv[1]) / "src"


def patch(rel, old, new, count=1):
    p = root / rel
    s = p.read_text()
    n = s.count(old)
    if n != count:
        sys.exit(f"{rel}: expected {count} match(es), found {n}: {old[:60]!r}")
    p.write_text(s.replace(old, new))


# 1. render-pass output remap
patch("internal_includes/toMetal.h", "    bool m_NeedFBOutputRemapDecl;", "    bool m_NeedFBOutputRemapDecl;\n    uint32_t m_FBOutputsUsed = 0;")
patch("toMetalDeclaration.cpp",
      'oss << type << " " << name << " [[ color(xlt_remap_o[" << psSignature->ui32SemanticIndex << "]) ]]";',
      'oss << type << " " << name << " [[ color(rp_output_remap_" << psSignature->ui32SemanticIndex << ") ]]";\n'
      '                    m_FBOutputsUsed |= 1u << psSignature->ui32SemanticIndex;')
patch("toMetal.cpp",
      '            bcatcstr(glsl, "#ifndef XLT_REMAP_O\\n\\t#define XLT_REMAP_O {0, 1, 2, 3, 4, 5, 6, 7}\\n#endif\\nconstexpr constant uint xlt_remap_o[] = XLT_REMAP_O;\\n");',
      '        {\n'
      '            bcatcstr(glsl, "constant uint32_t rp_output_remap_mask [[ function_constant(1) ]];\\n");\n'
      '            for (int i = 0; i < 8; i++)\n'
      '                if (m_FBOutputsUsed & (1u << i))\n'
      '                    bformata(glsl, "constant const uint rp_output_remap_%d = (rp_output_remap_mask >> %d) & 0xF;\\n", i, 4 * i);\n'
      '        }')

# 2. invariant position
patch("toMetalDeclaration.cpp", '"float4 mtl_Position [[ position ]]"', '"float4 mtl_Position [[ position, invariant ]]"', 2)
patch("toMetalDeclaration.cpp", 'oss << " [[ position ]]";', 'oss << " [[ position, invariant ]]";')

# 3. base vertex / instance behind function constant 4
patch("toMetalDeclaration.cpp", '"uint mtl_BaseInstance [[ base_instance ]]"', '"uint mtl_BaseInstance [[ base_instance, function_constant(has_base_vertex_instance) ]]"')
patch("toMetalDeclaration.cpp", '"uint mtl_BaseVertex [[ base_vertex ]]"', '"uint mtl_BaseVertex [[ base_vertex, function_constant(has_base_vertex_instance) ]]"')
for what, base in (("InstanceID", "BaseInstance"), ("VertexID", "BaseVertex")):
    patch("toMetal.cpp",
          f'                    bcatcstr(bodyglsl, "#if !UNITY_SUPPORT_INDIRECT_BUFFERS\\n");\n'
          f'                    psContext->AddIndentation();\n'
          f'                    bcatcstr(bodyglsl, "mtl_{base} = 0;\\n");\n'
          f'                    bcatcstr(bodyglsl, "#endif\\n");\n'
          f'                    psContext->AddIndentation();\n'
          f'                    bcatcstr(bodyglsl, "mtl_{what} = mtl_{what} - mtl_{base};\\n");',
          f'                    bcatcstr(bodyglsl, "    if(has_base_vertex_instance)\\n");\n'
          f'                    psContext->AddIndentation();\n'
          f'                    bcatcstr(bodyglsl, "    mtl_{what} = mtl_{what} - mtl_{base};\\n");')
# the function constant itself, declared once the vertex shader's builtin inputs are known
patch("toMetal.cpp",
      "        DeclareClipPlanes(&psShader->asPhases[0].psDecl[0], psShader->asPhases[0].psDecl.size());",
      "        if (psShader->eShaderType == VERTEX_SHADER)\n"
      "            for (const auto &mem : m_StructDefinitions[\"\"].m_Members)\n"
      "                if (mem.first == \"mtl_BaseVertex\" || mem.first == \"mtl_BaseInstance\")\n"
      "                {\n"
      "                    // the tessellation vertexFunction has it after the structs, right before the function\n"
      "                    bcatcstr((psContext->flags & HLSLCC_FLAG_METAL_TESSELLATION) ? bodyglsl : glsl, \"constant bool has_base_vertex_instance [[ function_constant(4) ]];\\n\");\n"
      "                    break;\n"
      "                }\n"
      "        DeclareClipPlanes(&psShader->asPhases[0].psDecl[0], psShader->asPhases[0].psDecl.size());")
# 4. multisampled texture arrays
patch("toMetalInstruction.cpp",
      '            psContext->m_Reflection.OnDiagnostics("Multisampled texture arrays not supported in Metal (in texel fetch)", 0, true);\n'
      '            return;',
      '            glsl << TranslateOperand(&psInst->asOperands[1], TO_FLAG_UNSIGNED_INTEGER | TO_AUTO_EXPAND_TO_VEC2, 3 /* .xy */);\n'
      '            if (psInst->bAddressOffset)\n'
      '                bformata(glsl, " + uint2(int2(%d, %d))", psInst->iUAddrOffset, psInst->iVAddrOffset);\n'
      '            bcatcstr(glsl, ", ");\n'
      '            glsl << TranslateOperand(&psInst->asOperands[1], TO_FLAG_UNSIGNED_INTEGER, OPERAND_4_COMPONENT_MASK_Z); // Array index\n'
      '            bcatcstr(glsl, ", ");\n'
      '            glsl << TranslateOperand(&psInst->asOperands[3], TO_FLAG_UNSIGNED_INTEGER, OPERAND_4_COMPONENT_MASK_X); // Sample index\n'
      '            break;', 2)  # with and without an address offset

# 5. primitive id
patch("toMetalDeclaration.cpp",
      '            case NAME_SAMPLE_INDEX:\n                result = "mtl_SampleID";',
      '            case NAME_PRIMITIVE_ID:\n'
      '                result = "mtl_PrimitiveID";\n'
      '                if (outSkipPrefix != NULL) *outSkipPrefix = true;\n'
      '                if (pui32IgnoreSwizzle)\n'
      '                    *pui32IgnoreSwizzle = 1;\n'
      '                return true;\n'
      '            case NAME_SAMPLE_INDEX:\n                result = "mtl_SampleID";')
patch("toMetalDeclaration.cpp",
      '[[ base_vertex, function_constant(has_base_vertex_instance) ]]")); // Requires Metal runtime 1.1+\n'
      '            break;\n'
      '        case NAME_PRIMITIVE_ID:\n'
      '            // Not on Metal\n'
      '            ASSERT(0);',
      '[[ base_vertex, function_constant(has_base_vertex_instance) ]]")); // Requires Metal runtime 1.1+\n'
      '            break;\n'
      '        case NAME_PRIMITIVE_ID:\n'
      '            m_StructDefinitions[""].m_Members.push_back(std::make_pair("mtl_PrimitiveID", "uint mtl_PrimitiveID [[ primitive_id ]]"));')
# 6. tessellation: post-tessellation function arguments and tess factor reads (has_base_vertex_instance: see 3)
patch("toMetal.cpp",
      '            m_StructDefinitions[""].m_Members.push_back(std::make_pair("input", GetInputStructName() + " input [[ stage_in ]]"));',
      '            if (psShader->eShaderType == DOMAIN_SHADER && (psContext->flags & HLSLCC_FLAG_METAL_TESSELLATION) != 0 && psContext->psDependencies)\n'
      '            {\n'
      '                m_StructDefinitions[""].m_Members.push_back(std::make_pair("patchID", "uint patchID [[ patch_id ]]"));\n'
      '                // the next free slots after every buffer of the three stages (the hull\'s 0xffff-n keys can collide\n'
      '                // with a reserved UAV key once the stage offset is added)\n'
      '                auto next = [&]() { uint32_t s = m_BufferSlots.PeekFirstFreeSlot(); m_BufferSlots.ReserveBindingSlot(s, BindingSlotAllocator::UAV); return s; };\n'
      '                bstring buffer = bfromcstr("");\n'
      '                if (hasControlPoint)\n'
      '                {\n'
      '                    bformata(buffer, "const device Mtl_ControlPoint *controlPoints [[ buffer(%d) ]]", next());\n'
      '                    m_StructDefinitions[""].m_Members.push_back(std::make_pair("controlPoints", (const char *)buffer->data));\n'
      '                    btrunc(buffer, 0);\n'
      '                }\n'
      '                if (hasPatchConstant)\n'
      '                {\n'
      '                    bformata(buffer, "const device Mtl_PatchConstant *patchConstants [[ buffer(%d) ]]", next());\n'
      '                    m_StructDefinitions[""].m_Members.push_back(std::make_pair("patchConstants", (const char *)buffer->data));\n'
      '                    btrunc(buffer, 0);\n'
      '                }\n'
      '                bformata(buffer, "const device %s *tessFactors [[ buffer(%d) ]]", psShader->sInfo.eTessDomain == TESSELLATOR_DOMAIN_QUAD ? "MTLQuadTessellationFactorsHalf" : "MTLTriangleTessellationFactorsHalf", next());\n'
      '                m_StructDefinitions[""].m_Members.push_back(std::make_pair("tessFactors", (const char *)buffer->data));\n'
      '                bdestroy(buffer);\n'
      '            }\n'
      '            m_StructDefinitions[""].m_Members.push_back(std::make_pair("input", GetInputStructName() + " input [[ stage_in ]]"));')
patch("toMetal.cpp",
      '            bcatcstr(bodyglsl, GetOutputStructName().c_str());\n'
      '            bcatcstr(bodyglsl, " output;\\n");\n'
      '        }\n',
      '            bcatcstr(bodyglsl, GetOutputStructName().c_str());\n'
      '            bcatcstr(bodyglsl, " output;\\n");\n'
      '        }\n'
      '        if (psShader->eShaderType == DOMAIN_SHADER && (psContext->flags & HLSLCC_FLAG_METAL_TESSELLATION) != 0 && psContext->psDependencies)\n'
      '        {\n'
      '            psContext->AddIndentation();\n'
      '            bformata(bodyglsl, "%s tessFactor;\\n", psShader->sInfo.eTessDomain == TESSELLATOR_DOMAIN_QUAD ? "MTLQuadTessellationFactorsHalf" : "MTLTriangleTessellationFactorsHalf");\n'
      '            psContext->AddIndentation();\n'
      '            bcatcstr(bodyglsl, "tessFactor = tessFactors[patchID];\\n");\n'
      '        }\n')
# in the domain stage the patch constant signature carries D3D_NAME values (13 tri edge, 14 tri inside), not the
# token names the switch below expects, so name tess factor inputs by semantic: SV_TessFactor[i], SV_InsideTessFactor[i]
patch("toMetalOperand.cpp",
      '            *piRebase = psIn->iRebase;\n'
      '            switch (psIn->eSystemValueType)',
      '            *piRebase = psIn->iRebase;\n'
      '            if (psContext->psShader->eShaderType == DOMAIN_SHADER && (psIn->semanticName == "SV_TessFactor" || psIn->semanticName == "SV_InsideTessFactor"))\n'
      '            {\n'
      '                const bool inside = psIn->semanticName == "SV_InsideTessFactor";\n'
      '                oss << (inside ? "tessFactor.insideTessellationFactor" : "tessFactor.edgeTessellationFactor");\n'
      '                if (!inside || psContext->psShader->sInfo.eTessDomain == TESSELLATOR_DOMAIN_QUAD)\n'
      '                    oss << "[" << psIn->ui32SemanticIndex << "]";\n'
      '                *pui32IgnoreSwizzle = 1;\n'
      '                break;\n'
      '            }\n'
      '            switch (psIn->eSystemValueType)')
# scalar control point inputs (a uint CUSTOM_INSTANCE_ID, say) are read without a swizzle where Unity's Metal build
# reads them so: in the domain stage, and in a hull whose control point phase declares them (that marks the
# register for its fork/join phases too; a pass-through hull keeps Unity's `.x` there). Nothing else marks them.
patch("toMetalDeclaration.cpp",
      '            int iNumComponents = psOperand->GetNumInputElements(psContext);\n'
      '            psShader->acInputDeclared[0][ui32Reg] = (char)psSig->ui32Mask;\n',
      '            int iNumComponents = psOperand->GetNumInputElements(psContext);\n'
      '            psShader->acInputDeclared[0][ui32Reg] = (char)psSig->ui32Mask;\n'
      '            if (iNumComponents == 1 && ((psOperand->eType == OPERAND_TYPE_INPUT_CONTROL_POINT && psShader->eShaderType == DOMAIN_SHADER) ||\n'
      '                                        (psOperand->eType == OPERAND_TYPE_INPUT && psShader->eShaderType == HULL_SHADER)))\n'
      '                psShader->abScalarInput[regSpace][ui32Reg] |= (int)ui32CompMask;\n')
# 7. stencil channel
patch("toMetalInstruction.cpp",
      '    bcatcstr(glsl, ")");\n\n'
      '    glsl << TranslateOperandSwizzle(&psInst->asOperands[2], psInst->asOperands[0].GetAccessMask(), 0);',
      '    bcatcstr(glsl, ")");\n\n'
      '    Operand resource = psInst->asOperands[2];\n'
      '    if (psBinding->name.find("Stencil") != std::string::npos &&\n'
      '        psContext->psShader->sInfo.GetTextureDataType(resource.ui32RegisterNumber) == SVT_UINT)\n'
      '        for (int c = 0; c < 4; c++)\n'
      '            if (resource.aui32Swizzle[c] == OPERAND_4_COMPONENT_Y) resource.aui32Swizzle[c] = OPERAND_4_COMPONENT_X;\n'
      '    glsl << TranslateOperandSwizzle(&resource, psInst->asOperands[0].GetAccessMask(), 0);', 2)  # texel fetch, with and without offset

# 8. 16-bit integer movs
patch("toMetalInstruction.cpp",
      '    uint32_t flags = SVTTypeToFlag(eSrcType);\n\n'
      '    AddAssignToDest(pDest, eSrcType, srcSwizzleCount, precise, numParenthesis);',
      '    uint32_t flags = SVTTypeToFlag(eSrcType);\n'
      '    SHADER_VARIABLE_TYPE eAssignType = eSrcType;\n'
      '    if (SVTTypeToResourceReturnType(pDest->GetDataType(psContext)) != RETURN_TYPE_FLOAT)\n'
      '        eAssignType = eSrcType == SVT_UINT16 ? SVT_UINT : eSrcType == SVT_INT16 ? SVT_INT : eSrcType;\n\n'
      '    AddAssignToDest(pDest, eAssignType, srcSwizzleCount, precise, numParenthesis);')
# 9. bfi operand widths
for k in (1, 2, 3, 4):
    patch("toMetalInstruction.cpp",
          f'            glsl << TranslateOperand(&psInst->asOperands[{k}], TO_FLAG_UNSIGNED_INTEGER, destMask);\n'
          + ('            bcatcstr(glsl, ")");\n\n            AddAssignPrologue(numParenthesis);\n            break;' if k == 4 else '            bcatcstr(glsl, ", ");'),
          f'            glsl << TranslateOperand(&psInst->asOperands[{k}], TO_FLAG_UNSIGNED_INTEGER | bfiExpand, destMask);\n'
          + ('            bcatcstr(glsl, ")");\n\n            AddAssignPrologue(numParenthesis);\n            break;' if k == 4 else '            bcatcstr(glsl, ", ");'))
patch("toMetalInstruction.cpp",
      '            uint32_t destMask = psInst->asOperands[0].GetAccessMask();\n'
      '            AddAssignToDest(&psInst->asOperands[0], SVT_UINT, psInst->asOperands[0].GetNumSwizzleElements(), psInst->ui32PreciseMask, numParenthesis);\n'
      '            bcatcstr(glsl, "bitFieldInsert(");',
      '            uint32_t destMask = psInst->asOperands[0].GetAccessMask();\n'
      '            const uint32_t bfiCount = psInst->asOperands[0].GetNumSwizzleElements();\n'
      '            const uint32_t bfiExpand = bfiCount > 1 ? (TO_AUTO_EXPAND_TO_VEC2 << (bfiCount - 2)) : 0;\n'
      '            AddAssignToDest(&psInst->asOperands[0], SVT_UINT, psInst->asOperands[0].GetNumSwizzleElements(), psInst->ui32PreciseMask, numParenthesis);\n'
      '            bcatcstr(glsl, "bitFieldInsert(");')

print("HLSLcc patched for Unity Metal conventions")
