/* A union returned by value. */
union fx_word
{
  int whole;
  unsigned short halves[2];
};

union fx_word fx_get_word(void);
