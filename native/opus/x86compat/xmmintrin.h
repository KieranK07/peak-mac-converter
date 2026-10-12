/* The Windows build (win32/config.h on MSVC x64) defines __SSE__, which switches CELT's float pitch kernels
   (xcorr_kernel, dual_inner_prod, celt_inner_prod, comb_filter_const in celt/x86/pitch_sse.h) to SSE
   intrinsics. Their 4-lane summation order decides the output bits, so the Mac compiles the same kernels:
   sse2neon maps each op they use (add/mul/shuffle/movehl/load/store) to a NEON op with identical IEEE results. */
#pragma once
#include "sse2neon.h"
