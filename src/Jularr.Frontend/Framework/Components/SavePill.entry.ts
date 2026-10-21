import { onPage } from "../../Modules/Navigation/PageSwap";
import { initializeSavePill, installSavePill } from "./SavePill";

installSavePill();
onPage(import.meta.url, initializeSavePill, { partial: true });
