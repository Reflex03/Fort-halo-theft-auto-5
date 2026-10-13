/*
 * Craft Theft Halo extractor (GPL-3.0): print every distinct Vorbis setup header that reference libvorbis produces for
 * 1-2 channels at the FSB5 sample rates and VBR qualities -0.1 .. 1.0 (step 0.01).
 * One line per header: crc32 seekBit channels rate quality hex(header)
 * seekBit = bit offset of the 6-bit "mode count - 1" field (what Fmod5Sharp's FmodVorbisData.SeekBit means), taken from
 * libvorbis' own mode count: modes are the last thing _vorbis_pack_books writes, followed only by the framing bit.
 * Build: see regen_vorbis_table.py (links libvorbis 1.3.7 + libogg 1.3.5 sources, BSD-3-Clause; nothing is shipped).
 */
#include <stdio.h>
#include <zlib.h>
#include <vorbis/vorbisenc.h>
#include "codec_internal.h"

static unsigned long seen[4096];
static int nseen;

int main(void) {
  static const long rates[] = {8000, 11000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000};
  for (int ch = 1; ch <= 2; ch++)
    for (unsigned ri = 0; ri < sizeof rates / sizeof rates[0]; ri++)
      for (int qi = -10; qi <= 100; qi++) {
        vorbis_info vi; vorbis_comment vc; vorbis_dsp_state vd; ogg_packet h, hc, hs;
        vorbis_info_init(&vi);
        if (vorbis_encode_init_vbr(&vi, ch, rates[ri], qi / 100.0f)) { vorbis_info_clear(&vi); continue; }
        vorbis_comment_init(&vc);
        vorbis_analysis_init(&vd, &vi);
        vorbis_analysis_headerout(&vd, &vc, &h, &hc, &hs);
        unsigned long crc = crc32(0L, hs.packet, hs.bytes);
        int dup = 0;
        for (int i = 0; i < nseen; i++) if (seen[i] == crc) { dup = 1; break; }
        if (!dup && nseen < 4096) {
          seen[nseen++] = crc;
          codec_setup_info *ci = (codec_setup_info *)vi.codec_setup;
          unsigned char last = hs.packet[hs.bytes - 1];
          int hb = 7; while (hb > 0 && !(last & (1 << hb))) hb--;   /* framing bit = highest set bit of the last byte */
          long framing = (long)(hs.bytes - 1) * 8 + hb;
          long seek = framing - 41L * ci->modes - 6;
          printf("%lu %ld %d %ld %.2f ", crc, seek, ch, rates[ri], qi / 100.0);
          for (long i = 0; i < hs.bytes; i++) printf("%02x", hs.packet[i]);
          printf("\n");
        }
        vorbis_dsp_clear(&vd); vorbis_comment_clear(&vc); vorbis_info_clear(&vi);
      }
  return 0;
}
