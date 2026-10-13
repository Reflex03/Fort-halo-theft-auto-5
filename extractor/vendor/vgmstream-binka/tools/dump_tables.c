/* Prints the float tables of vgmstream's src/coding/libs/binka_data.h as C# array initialisers (BinkaData.cs).
 * Part of the CthExtract vendoring of the vgmstream Bink Audio decoder (see ../VENDORED.md); not built or shipped.
 * usage: gcc -I<vgmstream>/src/coding/libs dump_tables.c -o dump && ./dump > tables.txt
 */
#include <stdio.h>
#include <stdint.h>
#include "binka_data.h"
static void dumpf(const char* name, const float* a, int n, int perline) {
    printf("    internal static readonly float[] %s =\n    [\n", name);
    for (int i = 0; i < n; i++) {
        if (i % perline == 0) printf("        ");
        char buf[64]; snprintf(buf, sizeof buf, "%.9g", a[i]);
        printf("%sf,%s", buf, (i % perline == perline - 1 || i == n - 1) ? "\n" : " ");
    }
    printf("    ];\n\n");
}
int main(void) {
    dumpf("Float29Power", binka_float29_power, 32, 8);
    printf("    internal static readonly ushort[] CutoffFrequency =\n    [\n        ");
    for (int i = 0; i < 25; i++) printf("%d,%s", binka_cutoff_frequency[i], i == 24 ? "\n" : (i % 8 == 7 ? "\n        " : " "));
    printf("    ];\n\n    internal static readonly byte[] RleTable =\n    [\n        ");
    for (int i = 0; i < 16; i++) printf("%d,%s", binka_rle_table[i], i == 15 ? "\n" : " ");
    printf("    ];\n\n");
    dumpf("Scalefactors", binka_scalefactors, 96, 8);
    dumpf("Cosines", binka_cosines, 256 * 10, 8);
    return 0;
}
