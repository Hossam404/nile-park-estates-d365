
var CommonGeneric = CommonGeneric || {};

CommonGeneric.GetFieldValue = function (formContext, fieldName) {
    var attribute = formContext.getAttribute(fieldName);
    return attribute ? attribute.getValue() : null;
};

CommonGeneric.SetFieldValue = function (formContext, fieldName, value) {
    var attribute = formContext.getAttribute(fieldName);
    if (attribute) {
        attribute.setValue(value);
    }
};

CommonGeneric.SetVisible = function (formContext, fieldName, isVisible) {
    var control = formContext.getControl(fieldName);
    if (control) {
        control.setVisible(isVisible);
    }
};

CommonGeneric.SetRequired = function (formContext, fieldName, isRequired) {
    var attribute = formContext.getAttribute(fieldName);
    if (attribute) {
        attribute.setRequiredLevel(isRequired ? "required" : "none");
    }
};

CommonGeneric.SetFocus = function (formContext, fieldName) {
    var control = formContext.getControl(fieldName);
    if (control) {
        control.setFocus();
    }
};

CommonGeneric.GetRecordId = function (formContext) {
    return formContext.data.entity.getId().replace(/[{}]/g, "");
};

CommonGeneric.IsCreateForm = function (formContext) {
    return formContext.ui.getFormType() === 1;
};

CommonGeneric.ShowFormNotification = function (formContext, message, level, uniqueId) {
    formContext.ui.setFormNotification(message, level, uniqueId);
};

CommonGeneric.ClearFormNotification = function (formContext, uniqueId) {
    formContext.ui.clearFormNotification(uniqueId);
};

CommonGeneric.DisableAllFields = function (formContext) {
    formContext.ui.controls.forEach(function (control) {
        if (control && control.getDisabled && !control.getDisabled()) {
            control.setDisabled(true);
        }
    });
};
