// Copyright (c) rAthena Dev Teams - Licensed under GNU GPL
// For more information, see LICENCE in the main folder

/**
 * Custom Atcommands
 * Place the body of custom atcommands in this file.
 * Format:
 *	ACMD_FUNC(command_name)
 *	{
 *		<code>;
 *	}
 **/

//ACMD_FUNC(newcommand)
//{
//	clif_displaymessage(fd, "It works!");
//	clif_specialeffect(&sd->bl, EF_HEARTCASTING, AREA);
//	return 0;
//}

ACMD_FUNC(menu)
{
	nullpo_retr(-1, sd);

	if (pc_isdead(sd)) {
		clif_displaymessage(fd, "你無法在死亡狀態下開啟隨身選單。");
		return -1;
	}

	if (sd->npc_id != 0) {
		clif_displaymessage(fd, "在開啟便攜選單之前，先關閉目前的 NPC 對話。");
		return -1;
	}

	npc_event(sd, "PortableMenu::OnMenu", 0);
	return 0;
}
