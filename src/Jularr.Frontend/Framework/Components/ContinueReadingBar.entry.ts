import { onPage } from "../../Modules/Navigation/PageSwap";
import { showContinueReading } from "./ContinueReadingBar";

onPage(import.meta.url, () => void showContinueReading());
