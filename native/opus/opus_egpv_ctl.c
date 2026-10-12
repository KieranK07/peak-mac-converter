/* The opus_egpv exports that aren't plain libopus: fixed-argument ctl entry points.

   PEAK's Windows-compiled PhotonVoice.API P/Invokes the variadic opus_encoder_ctl/opus_decoder_ctl as
   int f(IntPtr st, int request, int value) and int f(IntPtr st, int request, ref int value). Windows x64 passes
   varargs like fixed args, Apple arm64 passes them on the stack, so a variadic callee would read garbage.
   build.sh compiles libopus with its ctls renamed to *_ctl_va, and these take their names.
   The third argument arrives in x2 as an int (upper half undefined) or a pointer. It is forwarded as one
   8-byte vararg slot: libopus' va_arg(ap, opus_int32) reads its low half, va_arg(ap, T *) all of it.

   opus_*_ctl_set/_get: the 4 extra exports of the Windows DLL (Photon Voice's fixed-argument ctls). */
#include <stdint.h>
#define opus_encoder_ctl opus_encoder_ctl_va
#define opus_decoder_ctl opus_decoder_ctl_va
#include "opus.h"
#undef opus_encoder_ctl
#undef opus_decoder_ctl

OPUS_EXPORT int opus_encoder_ctl(OpusEncoder *st, int request, intptr_t value) {
  return opus_encoder_ctl_va(st, request, value);
}
OPUS_EXPORT int opus_decoder_ctl(OpusDecoder *st, int request, intptr_t value) {
  return opus_decoder_ctl_va(st, request, value);
}

OPUS_EXPORT int opus_encoder_ctl_set(OpusEncoder *st, int request, int value) {
  return opus_encoder_ctl_va(st, request, value);
}
OPUS_EXPORT int opus_encoder_ctl_get(OpusEncoder *st, int request, int *value) {
  return opus_encoder_ctl_va(st, request, value);
}
OPUS_EXPORT int opus_decoder_ctl_set(OpusDecoder *st, int request, int value) {
  return opus_decoder_ctl_va(st, request, value);
}
OPUS_EXPORT int opus_decoder_ctl_get(OpusDecoder *st, int request, int *value) {
  return opus_decoder_ctl_va(st, request, value);
}
