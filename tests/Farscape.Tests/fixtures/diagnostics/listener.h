/* A listener struct (#195): with wrapper generation, Farscape discovers it from its function
   pointer fields and refuses it (FS9163), since no generic listener builder exists. */
struct fx_listener {
    void (*on_row)(void *data, int row);
    void (*on_done)(void *data);
};

int fx_add_listener(struct fx_listener *listener, void *data);
