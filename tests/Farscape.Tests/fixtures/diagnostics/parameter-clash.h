/* Positional parameter names (#194): parameter 1 is named after the positional name of its
   unnamed sibling, parameter 0, so naming parameter 0 by position would bind 'arg0' twice
   (FS9131). */
int fx_clash(int, int arg0);
