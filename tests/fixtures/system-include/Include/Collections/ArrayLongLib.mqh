// ArrayLongLib.mqh — synthetic standard-library collection header (issue #179, S5).
//
// Project-authored test fixture: it reproduces the SHAPE of the real
// library's collection types — a long-array container with a count accessor
// (the CArrayLong pattern) — without containing any MetaQuotes/MetaEditor
// content. It sits two quoted-include hops below the entry header, so
// reaching it exercises a transitive include chain of depth > 1.
class CArrayLongSynth
{
public:
   int Total();
   long At(int index);
};
