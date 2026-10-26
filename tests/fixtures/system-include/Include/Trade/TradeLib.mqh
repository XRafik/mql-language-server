// TradeLib.mqh — synthetic standard-library trade header (issue #179, S5).
//
// Project-authored test fixture: it reproduces the SHAPE the real MetaTrader
// Standard Library exercises — a base class plus a derived trading class that
// consumer documents instantiate (the CTrade pattern) — without containing
// any MetaQuotes/MetaEditor content. It is reachable from consumer documents
// only through the Expert\ExpertEntry.mqh include closure.
class CSynthObjectBase
{
public:
   void Attach(int id);
};

class CSynthTrade : public CSynthObjectBase
{
public:
   bool Buy(double volume);
   bool Sell(double volume);
};
