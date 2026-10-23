import "./LiveProgress.scss";
import { installLive, syncLive } from "../../Modules/Live/Live";
import { onPage } from "../../Modules/Navigation/PageSwap";

installLive();
onPage(import.meta.url, syncLive, { partial: true });
