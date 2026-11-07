/* Issue #53 fixture: a packed enum is one byte; the record's size admits a wrong 4-byte extent. */
enum __attribute__((packed)) fx_packed { FX_PACKED_A, FX_PACKED_B = 200 };
struct fx_packed_rec {
    enum fx_packed kind;
    unsigned char tail;
    unsigned short pad;
};
